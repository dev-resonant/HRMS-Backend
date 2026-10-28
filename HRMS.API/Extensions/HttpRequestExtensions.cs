namespace HRMS.API.Extensions
{
    public static class HttpRequestExtensions
    {
        private const string BearerPrefix = "Bearer ";

        public static string? GetBearerToken(this HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();

            if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = header[BearerPrefix.Length..].Trim();

            return token.Length == 0 ? null : token;
        }
    }
}
