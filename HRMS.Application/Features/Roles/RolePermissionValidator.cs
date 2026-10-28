using System.Text;
using System.Text.Json;
using FluentValidation;
using HRMS.Domain.Authorization;

namespace HRMS.Application.Features.Roles
{
    /// <summary>One reason a proposed permissions document is refused. <see cref="Path"/> locates it (for example <c>employees[1]</c>).</summary>
    public sealed record PermissionDocumentError(string Path, string Code, string Message);

    /// <summary>
    /// Outcome of validating a proposed permissions document. When valid, <see cref="Permissions"/> and <see cref="CanonicalJson"/> hold the
    /// document in canonical form; when invalid both are null and nothing may be stored.
    /// </summary>
    public sealed class PermissionDocumentResult
    {
        public PermissionDocumentResult(IReadOnlyList<PermissionDocumentError> errors, IReadOnlyDictionary<string, string[]>? permissions, string? canonicalJson)
        {
            Errors = errors;
            Permissions = permissions;
            CanonicalJson = canonicalJson;
        }

        public IReadOnlyList<PermissionDocumentError> Errors { get; }

        public bool IsValid => Errors.Count == 0;

        public IReadOnlyDictionary<string, string[]>? Permissions { get; }

        public string? CanonicalJson { get; }
    }

    /// <summary>
    /// WRITE-side validation of a role's permissions document, to be used before anything is stored. The read side
    /// (<see cref="RolePermissions.Parse"/>) is deliberately lenient-but-closed: it silently drops whatever it does not recognise so a bad
    /// stored value can never grant anything. The write side is the opposite: it REFUSES, with a precise reason, anything that is not
    /// exactly <c>{ "module": ["action", ...] }</c> over the one <see cref="PermissionCatalog"/>, and never drops or repairs input.
    /// <para>Rules: valid JSON (no comments, no trailing commas, depth at most 4); root is an object (an empty object is allowed: a role with no
    /// permissions); every module and action exists in the catalog, compared ORDINALLY and case-sensitively with no trimming or coercion; a module is
    /// a non-empty array of strings (a module with nothing to grant is removed, not sent empty); no duplicate module and no duplicate action
    /// (including repeated JSON keys); limits of <see cref="MaxDocumentBytes"/>, <see cref="MaxModules"/> and <see cref="MaxActionsPerModule"/>.
    /// The canonical form is produced by the existing serializer (<see cref="RolePermissions.ToJson"/>): modules and actions in ordinal order, compact.</para>
    /// <para>This validates ONE document. Whether the result is acceptable for a built-in role is decided by <see cref="SystemRoleInvariants"/>.</para>
    /// </summary>
    public static class RolePermissionValidator
    {
        /// <summary>Assumption (the specification sets no limit): the whole catalog is well under 2 KB today; this leaves room for Phase 2 modules.</summary>
        public const int MaxDocumentBytes = 16 * 1024;

        public const int MaxModules = 64;

        public const int MaxActionsPerModule = 64;

        /// <summary>Errors reported at most; the rest are summarised so a hostile document cannot produce an unbounded response.</summary>
        public const int MaxErrors = 50;

        private const int MaxEchoLength = 64;

        public static PermissionDocumentResult Validate(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Fail("", "document_missing", "The permissions document is required.");
            }

            if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            {
                return Fail("", "document_too_large", $"The permissions document is larger than {MaxDocumentBytes} bytes.");
            }

            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    MaxDepth = 4,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });

                return Validate(document.RootElement);
            }
            catch (JsonException)
            {
                // Position and content are not echoed: the input is untrusted.
                return Fail("", "malformed_json", "The permissions document is not valid JSON (or is nested too deeply).");
            }
        }

        public static PermissionDocumentResult Validate(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("", "not_an_object", "The permissions document must be a JSON object of modules.");
            }

            // Checked first and on its own, so the limit is always reported (it would otherwise be crowded out by the per-module errors).
            if (root.EnumerateObject().Count() > MaxModules)
            {
                return Fail("", "too_many_modules", $"The permissions document has more than {MaxModules} modules.");
            }

            var errors = new List<PermissionDocumentError>();
            var modules = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            var seenModules = new HashSet<string>(StringComparer.Ordinal);

            foreach (var module in root.EnumerateObject())
            {

                var name = module.Name;
                var shown = Show(name);

                if (!seenModules.Add(name))
                {
                    Add(errors, shown, "duplicate_module", $"The module '{shown}' appears more than once.");
                    continue;
                }

                if (!PermissionCatalog.All.TryGetValue(name, out var knownActions))
                {
                    Add(errors, shown, "unknown_module", $"'{shown}' is not a permission module (names are case-sensitive).");
                    continue;
                }

                if (module.Value.ValueKind != JsonValueKind.Array)
                {
                    Add(errors, shown, "invalid_module_value", $"The module '{shown}' must be an array of action names.");
                    continue;
                }

                var length = module.Value.GetArrayLength();

                if (length == 0)
                {
                    Add(errors, shown, "empty_module", $"The module '{shown}' is empty; omit a module that grants nothing.");
                    continue;
                }

                if (length > MaxActionsPerModule)
                {
                    Add(errors, shown, "too_many_actions", $"The module '{shown}' has more than {MaxActionsPerModule} actions.");
                    continue;
                }

                var actions = new SortedSet<string>(StringComparer.Ordinal);
                var index = 0;

                foreach (var action in module.Value.EnumerateArray())
                {
                    var path = $"{shown}[{index++}]";

                    if (action.ValueKind != JsonValueKind.String)
                    {
                        Add(errors, path, "invalid_action_type", "An action must be a string.");
                        continue;
                    }

                    var value = action.GetString()!;

                    if (!knownActions.Contains(value, StringComparer.Ordinal))
                    {
                        Add(errors, path, "unknown_action", $"'{Show(value)}' is not an action of the module '{shown}' (names are case-sensitive).");
                        continue;
                    }

                    if (!actions.Add(value))
                    {
                        Add(errors, path, "duplicate_action", $"The action '{value}' appears more than once in '{shown}'.");
                    }
                }

                if (actions.Count > 0)
                {
                    modules[name] = actions.ToArray();
                }
            }

            if (errors.Count > 0)
            {
                return new PermissionDocumentResult(errors, null, null);
            }

            return new PermissionDocumentResult(Array.Empty<PermissionDocumentError>(), modules, RolePermissions.ToJson(modules));
        }

        /// <summary>FluentValidation rule: the property holds a valid permissions document. Each problem becomes its own failure.</summary>
        public static IRuleBuilderOptionsConditions<T, string?> ValidRolePermissions<T>(this IRuleBuilder<T, string?> rule)
            => rule.Custom((json, context) =>
            {
                foreach (var error in Validate(json).Errors)
                {
                    context.AddFailure(string.IsNullOrEmpty(error.Path) ? context.PropertyPath : $"{context.PropertyPath}.{error.Path}", error.Message);
                }
            });

        private static PermissionDocumentResult Fail(string path, string code, string message)
            => new(new[] { new PermissionDocumentError(path, code, message) }, null, null);

        private static void Add(List<PermissionDocumentError> errors, string path, string code, string message)
        {
            if (errors.Count < MaxErrors)
            {
                errors.Add(new PermissionDocumentError(path, code, message));
            }
            else if (errors.Count == MaxErrors)
            {
                errors.Add(new PermissionDocumentError("", "too_many_errors", "Further problems are not listed."));
            }
        }

        /// <summary>Untrusted names are echoed truncated and without control characters.</summary>
        private static string Show(string value)
        {
            var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());

            return clean.Length <= MaxEchoLength ? clean : clean[..MaxEchoLength] + "…";
        }
    }
}
