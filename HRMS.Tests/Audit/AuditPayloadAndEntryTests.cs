using System.Text.Json;
using HRMS.Application.Audit;
using HRMS.Application.Interfaces;

namespace HRMS.Tests.Audit;

public class AuditPayloadTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string BCryptHash = "$2a$12$abcdefghijklmnopqrstuuABCDEFGHIJKLMNOPQRSTUVWXYZ01234";   // 60 characters, bcrypt shape

    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement;
    }

    [Fact]
    public void Null_in_null_out()
        => Assert.Null(AuditPayload.ToJson(null));

    [Fact]
    public void An_ordinary_record_is_serialized_as_compact_camelCase_json()
    {
        var json = AuditPayload.ToJson(new { Name = "Asha", IsActive = true, Salary = 12.5m, Tags = new[] { "a", "b" } });

        Assert.Equal("{\"name\":\"Asha\",\"isActive\":true,\"salary\":12.5,\"tags\":[\"a\",\"b\"]}", json);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PasswordHash")]
    [InlineData("password_hash")]
    [InlineData("newPassword")]
    [InlineData("current-password")]
    [InlineData("confirmPassword")]
    [InlineData("temporaryPassword")]
    [InlineData("passwd")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("TokenHash")]
    [InlineData("sessionToken")]
    [InlineData("jwt")]
    [InlineData("Jwt")]
    [InlineData("clientSecret")]
    [InlineData("SECRET")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("Authorization")]
    [InlineData("connectionString")]
    [InlineData("credentials")]
    [InlineData("privateKey")]
    [InlineData("Cookie")]
    [InlineData("salt")]
    public void Credential_like_property_names_are_excluded(string name)
    {
        var json = AuditPayload.ToJson(new Dictionary<string, object?> { ["name"] = "Asha", [name] = "super-secret-value-123" });

        Assert.Equal("{\"name\":\"Asha\"}", json);
        Assert.DoesNotContain("super-secret-value-123", json);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("designation")]
    [InlineData("departmentId")]
    [InlineData("isActive")]
    [InlineData("status")]
    public void Ordinary_property_names_are_kept(string name)
    {
        var json = AuditPayload.ToJson(new Dictionary<string, object?> { [name] = "x" });

        Assert.Equal($"{{\"{name}\":\"x\"}}", json);
    }

    [Fact]
    public void Exclusion_applies_at_every_depth_and_inside_arrays()
    {
        var json = AuditPayload.ToJson(new
        {
            User = new { Name = "Asha", PasswordHash = "x", Profile = new { Bio = "hi", ApiKey = "k" } },
            Sessions = new object[] { new { Id = 1, TokenHash = "t" }, new { Id = 2, Token = "t2" } }
        });

        var root = Parse(json);
        Assert.Equal("Asha", root.GetProperty("user").GetProperty("name").GetString());
        Assert.False(root.GetProperty("user").TryGetProperty("passwordHash", out _));
        Assert.False(root.GetProperty("user").GetProperty("profile").TryGetProperty("apiKey", out _));
        Assert.Equal(2, root.GetProperty("sessions").GetArrayLength());
        Assert.All(root.GetProperty("sessions").EnumerateArray(), s => Assert.Single(s.EnumerateObject()));
        Assert.DoesNotContain("\"t\"", json);
        Assert.DoesNotContain("t2", json);
    }

    [Fact]
    public void Jwt_bearer_and_bcrypt_shaped_values_are_replaced_even_under_innocent_names()
    {
        var json = AuditPayload.ToJson(new
        {
            Note = Jwt,
            Header = "Bearer abc.def.ghi",
            Comment = BCryptHash,
            Items = new[] { Jwt, "fine" },
            Plain = "ordinary text"
        });

        var root = Parse(json);
        Assert.Equal(AuditPayload.Redacted, root.GetProperty("note").GetString());
        Assert.Equal(AuditPayload.Redacted, root.GetProperty("header").GetString());
        Assert.Equal(AuditPayload.Redacted, root.GetProperty("comment").GetString());
        Assert.Equal(AuditPayload.Redacted, root.GetProperty("items")[0].GetString());
        Assert.Equal("fine", root.GetProperty("items")[1].GetString());
        Assert.Equal("ordinary text", root.GetProperty("plain").GetString());
        Assert.DoesNotContain("eyJ", json);
        Assert.DoesNotContain("$2a$", json);
    }

    [Fact]
    public void A_real_user_entity_snapshot_never_contains_the_hash_or_token_fields()
    {
        var user = new HRMS.Domain.Entities.User
        {
            Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), RoleId = Guid.NewGuid(), Name = "Asha",
            Email = "asha@example.test", PasswordHash = BCryptHash, IsActive = true
        };

        var json = AuditPayload.ToJson(user)!;

        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BCryptHash, json);
        Assert.Contains("asha@example.test", json);
    }

    [Fact]
    public void The_payload_is_a_snapshot_not_a_reference()
    {
        var source = new Dictionary<string, object?> { ["name"] = "before" };
        var json = AuditPayload.ToJson(source);

        source["name"] = "after";

        Assert.Equal("{\"name\":\"before\"}", json);
    }

    [Fact]
    public void Reference_cycles_do_not_hang_or_crash()
    {
        var a = new Node { Name = "a" };
        a.Next = new Node { Name = "b", Next = a };

        var json = AuditPayload.ToJson(a);

        Assert.Contains("\"name\":\"a\"", json);
    }

    private sealed class Node
    {
        public string Name { get; set; } = string.Empty;

        public Node? Next { get; set; }
    }

    [Fact]
    public void Oversized_payloads_are_refused_not_truncated()
    {
        var huge = new { Text = new string('x', AuditPayload.MaxLength + 1) };

        Assert.Throws<InvalidOperationException>(() => AuditPayload.ToJson(huge));
    }

    [Fact]
    public void Output_is_always_valid_json()
    {
        var json = AuditPayload.ToJson(new { Quote = "he said \"hi\"\n\t", Unicode = "नमस्ते", Number = 1, Nothing = (string?)null });

        var root = Parse(json);
        Assert.Equal("he said \"hi\"\n\t", root.GetProperty("quote").GetString());
        Assert.Equal("नमस्ते", root.GetProperty("unicode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nothing").ValueKind);
    }
}

public class AuditEntryTests
{
    [Theory]
    [InlineData("employee", "employee.created")]
    [InlineData("attendance", "punch.in")]
    [InlineData("leave", "leave.approved")]
    [InlineData("auth", "auth.login_failed")]
    [InlineData("role", "role.permissions_updated")]
    public void Spec_style_names_are_accepted(string module, string action)
    {
        var entry = new AuditEntry(module, action);

        Assert.Equal(module, entry.Module);
        Assert.Equal(action, entry.Action);
        Assert.Null(entry.TargetId);
        Assert.Null(entry.OldValueJson);
        Assert.Null(entry.NewValueJson);
    }

    [Theory]
    [InlineData("", "employee.created")]
    [InlineData("Employee", "employee.created")]
    [InlineData("employee-x", "employee.created")]
    [InlineData("1employee", "employee.created")]
    [InlineData("employee", "")]
    [InlineData("employee", "created")]                 // not dotted
    [InlineData("employee", "Employee.Created")]
    [InlineData("employee", "employee.")]
    [InlineData("employee", ".created")]
    [InlineData("employee", "employee created")]
    public void Malformed_names_are_rejected(string module, string action)
        => Assert.Throws<ArgumentException>(() => new AuditEntry(module, action));

    [Fact]
    public void Names_longer_than_the_columns_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new AuditEntry(new string('a', AuditEntry.MaxModuleLength + 1), "a.b"));
        Assert.Throws<ArgumentException>(() => new AuditEntry("a", "a." + new string('b', AuditEntry.MaxActionLength)));
        _ = new AuditEntry(new string('a', AuditEntry.MaxModuleLength), "a." + new string('b', AuditEntry.MaxActionLength - 2));   // exactly at the limit
    }

    [Fact]
    public void An_empty_target_id_is_rejected_but_null_is_allowed_for_system_actions()
    {
        Assert.Throws<ArgumentException>(() => new AuditEntry("employee", "employee.created", Guid.Empty));
        Assert.Null(new AuditEntry("employee", "employee.created", null).TargetId);
    }

    [Fact]
    public void Old_and_new_values_are_sanitized_snapshots_taken_when_the_entry_is_created()
    {
        var entry = new AuditEntry("employee", "employee.updated", Guid.NewGuid(),
            oldValue: new { Name = "A", PasswordHash = "x" }, newValue: new { Name = "B", Token = "y" });

        Assert.Equal("{\"name\":\"A\"}", entry.OldValueJson);
        Assert.Equal("{\"name\":\"B\"}", entry.NewValueJson);
    }

    [Fact]
    public void An_entry_carries_no_actor_company_or_ip_so_the_client_cannot_supply_them()
    {
        var names = typeof(AuditEntry).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Action", "Module", "NewValueJson", "OldValueJson", "TargetId" }, names);
    }

    [Fact]
    public void The_audit_writer_contract_has_no_update_or_delete_operation()
    {
        var methods = typeof(IAuditWriter).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { nameof(IAuditWriter.RecordFailedLoginAsync), nameof(IAuditWriter.Stage) }, methods);
    }
}
