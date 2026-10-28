using HRMS.Application.Common.Exceptions;
using Microsoft.Net.Http.Headers;

namespace HRMS.API.Extensions
{
    /// <summary>
    /// ETag / If-Match for employees. The ETag is the opaque row version (PostgreSQL xmin) in quotes. If-Match is OPTIONAL: when absent
    /// the behavior is unchanged (the server-side concurrency token still protects each request); when present it is enforced, and a
    /// mismatch answers 412. "*" means "any current version". A weak tag (W/"...") or one that is not a version never matches.
    /// </summary>
    public static class ETagHeaders
    {
        public static string For(uint version) => $"\"{version}\"";

        /// <summary>null = no precondition (header absent or "*"); otherwise the versions the client will accept (possibly none).</summary>
        public static IReadOnlyList<uint>? ReadIfMatch(HttpRequest request)
        {
            if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var raw) || raw.Count == 0)
            {
                return null;
            }

            var tags = request.GetTypedHeaders().IfMatch;

            if (tags.Count == 0)
            {
                throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure("If-Match", "If-Match is not a valid entity-tag list.") });
            }

            if (tags.Any(t => t.Equals(EntityTagHeaderValue.Any)))
            {
                return null;
            }

            return tags
                .Where(t => !t.IsWeak && uint.TryParse(t.Tag.Value?.Trim('"'), out _))
                .Select(t => uint.Parse(t.Tag.Value!.Trim('"')))
                .ToList();
        }
    }
}
