using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace HRMS.API.Extensions
{
    /// <summary>
    /// Browser access policy "Frontend". The allowed origins come from configuration (<c>Cors:AllowedOrigins</c>, an array or one
    /// comma-separated string such as the environment variable <c>Cors__AllowedOrigins</c>); nothing is hard-coded and no origin is ever
    /// allowed implicitly.
    /// <list type="bullet">
    /// <item>Each entry must be an absolute origin: scheme + host (+ port), no path, query, fragment or credentials. <c>*</c> is refused.</item>
    /// <item>Production requires at least one origin and only <c>https</c>; a missing or invalid value stops the application at start.</item>
    /// <item>Development with nothing configured falls back to the Vite default <c>http://localhost:5173</c>; any other environment with
    /// nothing configured allows no browser origin at all (closed).</item>
    /// <item>Credentials are never allowed (the API uses a bearer header, not cookies), so there is no wildcard-with-credentials risk.</item>
    /// </list>
    /// </summary>
    public static class CorsSetup
    {
        public const string PolicyName = "Frontend";
        public const string SectionKey = "Cors:AllowedOrigins";
        public const string DevelopmentDefaultOrigin = "http://localhost:5173";

        public static IServiceCollection ConfigureFrontendCors(this IServiceCollection services)
        {
            // Bound from the built host's configuration and environment (like the other options); the policy itself is built by Resolve.
            services.AddOptions<CorsOptions>().Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            {
                var origins = ResolveOrigins(configuration, environment.EnvironmentName);

                options.AddPolicy(PolicyName, policy =>
                {
                    if (origins.Count > 0)
                    {
                        policy.WithOrigins(origins.ToArray());
                    }
                    else
                    {
                        policy.SetIsOriginAllowed(_ => false);          // closed: no origin, never a wildcard
                    }

                    policy.AllowAnyHeader()
                          .AllowAnyMethod()
                          .WithExposedHeaders("ETag", "Retry-After")
                          .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                });
            });

            return services;
        }

        /// <summary>Forces the options to be built now, so an invalid Production configuration stops the application at start.</summary>
        public static void ValidateFrontendCors(this IServiceProvider services) => _ = services.GetRequiredService<IOptions<CorsOptions>>().Value;

        public static IReadOnlyList<string> ResolveOrigins(IConfiguration configuration, string environmentName)
        {
            var production = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
            var development = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

            var raw = new List<string>();
            var section = configuration.GetSection(SectionKey);

            if (section.Value is { } single)
            {
                raw.Add(single);
            }

            raw.AddRange(section.GetChildren().Select(c => c.Value).OfType<string>());

            var entries = raw.SelectMany(r => r.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();

            if (entries.Count == 0)
            {
                if (production)
                {
                    throw new InvalidOperationException($"{SectionKey} is not configured. Production requires the explicit https origin(s) of the frontend (for example Cors__AllowedOrigins__0).");
                }

                return development ? new[] { DevelopmentDefaultOrigin } : Array.Empty<string>();
            }

            return entries.Select(e => Normalize(e, production)).Distinct(StringComparer.Ordinal).ToList();
        }

        private static string Normalize(string entry, bool requireHttps)
        {
            if (entry == "*" || !Uri.TryCreate(entry, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || (uri.AbsolutePath != "/" && uri.AbsolutePath != string.Empty)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidOperationException($"{SectionKey} contains an invalid origin '{entry}'. Use scheme://host[:port] only (no wildcard, path, query or credentials).");
            }

            if (requireHttps && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException($"{SectionKey} contains the non-https origin '{entry}'. Production allows https origins only.");
            }

            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
    }
}
