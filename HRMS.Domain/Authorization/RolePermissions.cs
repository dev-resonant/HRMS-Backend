using System.Text.Json;

namespace HRMS.Domain.Authorization
{
    /// <summary>
    /// Reads and writes the permissions JSON of a role. Parsing is deliberately strict and fails closed: anything that is not
    /// an object of string arrays, and any module/action that is not in the <see cref="PermissionCatalog"/>, grants nothing.
    /// </summary>
    public static class RolePermissions
    {
        public static IReadOnlyDictionary<string, string[]> Empty { get; } = new Dictionary<string, string[]>();

        public static IReadOnlyDictionary<string, string[]> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Empty;
                }

                var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

                foreach (var module in document.RootElement.EnumerateObject())
                {
                    if (module.Value.ValueKind != JsonValueKind.Array || !PermissionCatalog.All.ContainsKey(module.Name))
                    {
                        continue;
                    }

                    var actions = new SortedSet<string>(StringComparer.Ordinal);

                    foreach (var action in module.Value.EnumerateArray())
                    {
                        if (action.ValueKind == JsonValueKind.String && PermissionCatalog.IsValid(module.Name, action.GetString()))
                        {
                            actions.Add(action.GetString()!);
                        }
                    }

                    if (actions.Count > 0)
                    {
                        result[module.Name] = actions.ToArray();
                    }
                }

                return result;
            }
            catch (JsonException)
            {
                return Empty;
            }
        }

        public static string ToJson(IReadOnlyDictionary<string, string[]> permissions)
            => JsonSerializer.Serialize(permissions.OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => p.Value.OrderBy(a => a, StringComparer.Ordinal).ToArray()));
    }
}
