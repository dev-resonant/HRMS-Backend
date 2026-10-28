using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HRMS.Application.Audit
{
    /// <summary>
    /// Turns the "state before / after" of a record into the JSON stored in audit_log.old_value / new_value, and removes
    /// credentials on the way: any property whose name looks like a secret is dropped (at any depth, in objects and inside
    /// arrays), and any string value that looks like a JWT, a bearer header or a BCrypt hash is replaced by a placeholder.
    /// The result is an independent snapshot - later changes to the source object do not alter it.
    /// </summary>
    public static class AuditPayload
    {
        public const string Redacted = "[REDACTED]";

        /// <summary>Payloads above this many characters are refused instead of silently truncated (a truncated "full state" would be misleading).</summary>
        public const int MaxLength = 262_144;

        private const int MaxDepth = 32;

        // Compared against the property name lower-cased with every non-alphanumeric removed (password_hash -> passwordhash).
        // Deliberately broad: it is safer to omit an unusual field than to write a credential into an immutable log.
        private static readonly string[] SensitiveNameFragments =
        {
            "password", "passwd", "pwd", "secret", "token", "jwt", "apikey", "authorization", "credential",
            "privatekey", "connectionstring", "cookie", "salt", "hash"
        };

        private static readonly Regex JwtLike = new(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex BearerLike = new(@"^\s*Bearer\s+\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex BCryptLike = new(@"^\$2[abxy]?\$\d{2}\$[./A-Za-z0-9]{53}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = MaxDepth
        };

        public static bool IsSensitiveName(string name)
        {
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

            return SensitiveNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
        }

        /// <summary>Null in, null out. Otherwise compact, sanitized JSON.</summary>
        public static string? ToJson(object? value)
        {
            if (value is null)
            {
                return null;
            }

            var element = JsonSerializer.SerializeToElement(value, SerializerOptions);

            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(element, writer, 0);
            }

            var json = Encoding.UTF8.GetString(stream.ToArray());

            if (json.Length > MaxLength)
            {
                throw new InvalidOperationException($"The audit payload is larger than {MaxLength} characters and was not recorded.");
            }

            return json;
        }

        private static void Write(JsonElement element, Utf8JsonWriter writer, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException("The audit payload is nested too deeply.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var property in element.EnumerateObject())
                    {
                        if (IsSensitiveName(property.Name))
                        {
                            continue;
                        }

                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer, depth + 1);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (var item in element.EnumerateArray())
                    {
                        Write(item, writer, depth + 1);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    var text = element.GetString() ?? string.Empty;
                    writer.WriteStringValue(LooksLikeSecret(text) ? Redacted : text);
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static bool LooksLikeSecret(string text)
            => JwtLike.IsMatch(text) || BearerLike.IsMatch(text) || BCryptLike.IsMatch(text);
    }
}
