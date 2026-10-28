using System.Globalization;
using System.Text.Json;
using FluentValidation;
using HRMS.Application.Features.Roles;
using HRMS.Domain.Authorization;

namespace HRMS.Tests.Roles;

/// <summary>Write-side validation of a proposed permissions document: strict, never silently repairing, canonical output.</summary>
public class RolePermissionValidatorTests
{
    private static string CodeOf(string json)
    {
        var result = RolePermissionValidator.Validate(json);
        Assert.False(result.IsValid, $"expected a rejection of: {json}");
        Assert.Null(result.Permissions);                                                     // nothing usable comes out of a rejected document
        Assert.Null(result.CanonicalJson);
        return string.Join(",", result.Errors.Select(e => e.Code).Distinct());
    }

    // ================= accepted =================

    [Fact]
    public void Every_built_in_roles_stored_json_is_valid_and_already_canonical()
    {
        foreach (var definition in SystemRoles.Definitions)
        {
            var result = RolePermissionValidator.Validate(definition.PermissionsJson);

            Assert.True(result.IsValid, definition.Key);
            Assert.Equal(definition.PermissionsJson, result.CanonicalJson);                  // the validator and the seeded rows agree byte for byte
            Assert.Equal(definition.Permissions.Count, result.Permissions!.Count);
        }
    }

    [Fact]
    public void An_empty_object_is_a_valid_role_with_no_permissions()
    {
        var result = RolePermissionValidator.Validate("{}");

        Assert.True(result.IsValid);
        Assert.Equal("{}", result.CanonicalJson);
        Assert.Empty(result.Permissions!);
    }

    [Fact]
    public void Input_order_and_whitespace_do_not_matter_and_the_output_is_canonical_and_stable()
    {
        var a = RolePermissionValidator.Validate("{ \"employees\" : [\"view_team\", \"view_own\"],\n \"departments\":[\"manage\",\"view_all\"] }");
        var b = RolePermissionValidator.Validate("{\"departments\":[\"view_all\",\"manage\"],\"employees\":[\"view_own\",\"view_team\"]}");

        Assert.True(a.IsValid && b.IsValid);
        Assert.Equal("{\"departments\":[\"manage\",\"view_all\"],\"employees\":[\"view_own\",\"view_team\"]}", a.CanonicalJson);
        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        Assert.Equal(a.CanonicalJson, RolePermissionValidator.Validate(a.CanonicalJson).CanonicalJson);   // idempotent
    }

    [Fact]
    public void A_parsed_element_can_be_validated_without_re_serializing()
    {
        using var document = JsonDocument.Parse("{\"roles\":[\"view\"]}");

        var result = RolePermissionValidator.Validate(document.RootElement);

        Assert.True(result.IsValid);
        Assert.Equal("{\"roles\":[\"view\"]}", result.CanonicalJson);
    }

    // ================= rejected =================

    [Theory]
    [InlineData("", "document_missing")]
    [InlineData("   ", "document_missing")]
    [InlineData("{", "malformed_json")]
    [InlineData("{\"employees\":[\"view_own\"]", "malformed_json")]
    [InlineData("not json", "malformed_json")]
    [InlineData("{\"employees\":[\"view_own\"],}", "malformed_json")]          // trailing comma
    [InlineData("{/*c*/\"employees\":[\"view_own\"]}", "malformed_json")]      // comment
    [InlineData("{'employees':['view_own']}", "malformed_json")]               // single quotes
    [InlineData("{\"employees\":[[[[\"view_own\"]]]]}", "malformed_json")]     // nested deeper than the format allows
    public void Malformed_or_missing_documents_are_refused(string json, string expected)
        => Assert.Equal(expected, CodeOf(json));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"employees\"]")]
    [InlineData("\"employees\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void A_root_that_is_not_an_object_is_refused(string json)
        => Assert.Equal("not_an_object", CodeOf(json));

    [Theory]
    [InlineData("{\"employees\":null}", "invalid_module_value")]
    [InlineData("{\"employees\":\"view_own\"}", "invalid_module_value")]
    [InlineData("{\"employees\":5}", "invalid_module_value")]
    [InlineData("{\"employees\":true}", "invalid_module_value")]
    [InlineData("{\"employees\":{\"view_own\":true}}", "invalid_module_value")]
    [InlineData("{\"employees\":[]}", "empty_module")]
    [InlineData("{\"employees\":[null]}", "invalid_action_type")]
    [InlineData("{\"employees\":[1]}", "invalid_action_type")]
    [InlineData("{\"employees\":[true]}", "invalid_action_type")]
    [InlineData("{\"employees\":[[\"view_own\"]]}", "invalid_action_type")]
    [InlineData("{\"employees\":[{\"a\":1}]}", "invalid_action_type")]
    public void Wrong_value_types_are_refused(string json, string expected)
        => Assert.Equal(expected, CodeOf(json));

    [Theory]
    [InlineData("{\"bogus\":[\"view_own\"]}", "unknown_module")]
    [InlineData("{\"attendance\":[\"punch\"]}", "unknown_module")]                         // the specification's own example module is not in the catalog yet
    [InlineData("{\"employees\":[\"fly\"]}", "unknown_action")]
    [InlineData("{\"employees\":[\"manage\"]}", "unknown_action")]                         // exists, but for another module
    [InlineData("{\"employees\":[\"view_own\",\"fly\"]}", "unknown_action")]               // one bad action refuses the whole document
    [InlineData("{\"employees\":[\"view_own\"],\"bogus\":[\"x\"]}", "unknown_module")]
    public void Unknown_modules_and_actions_are_refused_never_dropped(string json, string expected)
        => Assert.Equal(expected, CodeOf(json));

    [Theory]
    [InlineData("{\"Employees\":[\"view_own\"]}", "unknown_module")]                       // names are case-sensitive
    [InlineData("{\"EMPLOYEES\":[\"view_own\"]}", "unknown_module")]
    [InlineData("{\"employees\":[\"VIEW_OWN\"]}", "unknown_action")]
    [InlineData("{\"employees\":[\"View_Own\"]}", "unknown_action")]
    [InlineData("{\"employees \":[\"view_own\"]}", "unknown_module")]                      // no trimming
    [InlineData("{\"employees\":[\" view_own\"]}", "unknown_action")]
    [InlineData("{\"employees\":[\"view_own\\u0000\"]}", "unknown_action")]
    [InlineData("{\"employees\":[\"view_пwn\"]}", "unknown_action")]                  // Cyrillic look-alike letter
    [InlineData("{\"employees\":[\"view\\u005Fown\\n\"]}", "unknown_action")]              // escaped underscore + newline
    public void Matching_is_exact_ordinal_with_no_case_folding_trimming_or_coercion(string json, string expected)
        => Assert.Equal(expected, CodeOf(json));

    [Theory]
    [InlineData("{\"employees\":[\"view_own\",\"view_own\"]}", "duplicate_action")]
    [InlineData("{\"employees\":[\"view_own\",\"view_team\",\"view_own\"]}", "duplicate_action")]
    [InlineData("{\"employees\":[\"view_own\"],\"employees\":[\"view_team\"]}", "duplicate_module")]   // repeated JSON key: a parser would keep only one
    public void Duplicates_are_refused(string json, string expected)
        => Assert.Equal(expected, CodeOf(json));

    [Fact]
    public void The_culture_of_the_server_cannot_change_what_is_accepted()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");                           // the classic dotted/dotless I culture

            Assert.True(RolePermissionValidator.Validate("{\"employees\":[\"view_own\"]}").IsValid);
            Assert.Equal("unknown_action", CodeOf("{\"employees\":[\"VIEW_OWN\"]}"));
            Assert.Equal("unknown_module", CodeOf("{\"EMPLOYEES\":[\"view_own\"]}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ================= limits =================

    [Fact]
    public void An_oversized_document_is_refused_before_it_is_parsed()
    {
        var json = "{\"employees\":[\"view_own\"],\"padding\":\"" + new string('x', RolePermissionValidator.MaxDocumentBytes) + "\"}";

        Assert.Equal("document_too_large", CodeOf(json));
    }

    [Fact]
    public void The_size_limit_counts_bytes_not_characters()
    {
        var json = "{\"x\":\"" + new string('€', RolePermissionValidator.MaxDocumentBytes / 3 + 10) + "\"}";   // 3 bytes each in UTF-8, fewer characters than the limit

        Assert.True(json.Length < RolePermissionValidator.MaxDocumentBytes);
        Assert.Equal("document_too_large", CodeOf(json));
    }

    [Fact]
    public void Too_many_modules_or_actions_are_refused()
    {
        var modules = "{" + string.Join(",", Enumerable.Range(0, RolePermissionValidator.MaxModules + 1).Select(i => $"\"m{i}\":[\"a\"]")) + "}";
        var actions = "{\"employees\":[" + string.Join(",", Enumerable.Repeat("\"view_own\"", RolePermissionValidator.MaxActionsPerModule + 1)) + "]}";

        Assert.Equal("too_many_modules", CodeOf(modules));
        Assert.Equal("too_many_actions", CodeOf(actions));
    }

    [Fact]
    public void The_number_of_reported_errors_is_bounded()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, RolePermissionValidator.MaxModules).Select(i => $"\"m{i}\":[\"a\"]")) + "}";   // exactly the allowed number of modules, all unknown

        var result = RolePermissionValidator.Validate(json);

        Assert.Equal(RolePermissionValidator.MaxErrors + 1, result.Errors.Count);
        Assert.Equal("too_many_errors", result.Errors[^1].Code);
    }

    [Fact]
    public void Untrusted_names_are_echoed_truncated_without_control_characters_and_malformed_input_is_not_echoed_at_all()
    {
        // Control characters inside a JSON string must be escaped to be valid JSON; the decoded name then contains them.
        var unknown = RolePermissionValidator.Validate("{\"evil" + new string('z', 300) + "\\u0001\\u0007\":[\"a\"]}");
        var malformed = RolePermissionValidator.Validate("{\"super-secret-value");

        var shown = Assert.Single(unknown.Errors);
        Assert.True(shown.Message.Length < 200);
        Assert.DoesNotContain(shown.Message, c => char.IsControl(c));
        Assert.Contains("…", shown.Path);
        Assert.DoesNotContain("super-secret-value", string.Join(" ", malformed.Errors.Select(e => e.Message + e.Path)));
    }

    // ================= write side versus read side =================

    [Fact]
    public void The_read_side_still_drops_what_the_write_side_refuses()
    {
        const string stored = "{\"employees\":[\"view_own\",\"fly\"],\"bogus\":[\"x\"],\"departments\":\"manage\"}";

        // Read side (unchanged): fail closed - grants only what is recognised, never throws.
        var read = RolePermissions.Parse(stored);
        Assert.Equal(new[] { "employees" }, read.Keys);
        Assert.Equal(new[] { "view_own" }, read["employees"]);

        // Write side: refuses the document and reports every problem; nothing is repaired.
        var result = RolePermissionValidator.Validate(stored);
        Assert.False(result.IsValid);
        Assert.Equal(new[] { "invalid_module_value", "unknown_action", "unknown_module" }, result.Errors.Select(e => e.Code).Distinct().OrderBy(c => c));
    }

    [Fact]
    public void Everything_the_validator_accepts_is_read_back_unchanged_by_the_read_side()
    {
        var all = "{" + string.Join(",", PermissionCatalog.All.Select(m => $"\"{m.Key}\":[{string.Join(",", m.Value.Select(a => $"\"{a}\""))}]")) + "}";   // every catalog permission

        var result = RolePermissionValidator.Validate(all);

        Assert.True(result.IsValid);
        Assert.Equal(result.CanonicalJson, RolePermissions.ToJson(RolePermissions.Parse(result.CanonicalJson)));
        Assert.Equal(PermissionCatalog.All.Sum(m => m.Value.Count), result.Permissions!.Sum(m => m.Value.Length));
    }

    // ================= FluentValidation integration =================

    private sealed record Body(string? Permissions);

    private sealed class BodyValidator : AbstractValidator<Body>
    {
        public BodyValidator() => RuleFor(b => b.Permissions).ValidRolePermissions();
    }

    [Fact]
    public void The_fluent_rule_reports_each_problem_under_the_property_and_accepts_a_valid_document()
    {
        var validator = new BodyValidator();

        Assert.True(validator.Validate(new Body("{\"employees\":[\"view_own\"]}")).IsValid);

        var invalid = validator.Validate(new Body("{\"employees\":[\"fly\"],\"bogus\":[\"x\"]}"));
        Assert.False(invalid.IsValid);
        Assert.Equal(2, invalid.Errors.Count);
        Assert.All(invalid.Errors, e => Assert.StartsWith("Permissions.", e.PropertyName));

        var missing = validator.Validate(new Body(null));
        Assert.False(missing.IsValid);
        Assert.Equal("Permissions", Assert.Single(missing.Errors).PropertyName);
    }
}
