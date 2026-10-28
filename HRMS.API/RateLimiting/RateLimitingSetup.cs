using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HRMS.Application.Common.Exceptions;
using Microsoft.AspNetCore.RateLimiting;

namespace HRMS.API.RateLimiting
{
    public static class RateLimitingSetup
    {
        /// <summary>
        /// Registers the ASP.NET Core rate limiter. One chained global limiter (every member must grant a permit) is used instead of
        /// named policies because an endpoint needs several independent keys at once - address, authenticated user, target - and a named
        /// policy can only contribute one partition. Endpoints opt in with <see cref="AuthRateLimitAttribute"/>.
        /// </summary>
        public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
        {
            services.AddSingleton<AttemptLimiter>();
            services.AddRateLimiter(_ => { });

            // The settings come from the container (and so from the built host's configuration); the limiters read them on every request.
            services.AddOptions<RateLimiterOptions>().Configure<RateLimitingOptions>((options, settings) =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = WriteRejectionAsync;

                options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                    ByAddress(settings),
                    ByAuthenticatedUser(settings),
                    ByTarget(settings));
            });

            return services;
        }

        private static AuthRateLimitPolicy? PolicyOf(HttpContext context)
            => context.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimitAttribute>()?.Policy;

        private static PartitionedRateLimiter<HttpContext> ByAddress(RateLimitingOptions settings)
            => PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!settings.Enabled || PolicyOf(context) is not { } policy)
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                var rule = policy switch
                {
                    AuthRateLimitPolicy.Login => settings.LoginPerIp,
                    AuthRateLimitPolicy.SetPassword => settings.SetPasswordPerIp,
                    AuthRateLimitPolicy.ChangePassword => settings.ChangePasswordPerIp,
                    _ => settings.InvitationReissuePerIp
                };

                return Window($"{policy}|ip|{AddressKey(context.Connection.RemoteIpAddress)}", rule);
            });

        /// <summary>Per authenticated user. Anonymous requests have no user and are covered by the address limiter alone.</summary>
        private static PartitionedRateLimiter<HttpContext> ByAuthenticatedUser(RateLimitingOptions settings)
            => PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (!settings.Enabled || userId is null || PolicyOf(context) is not { } policy)
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                return policy switch
                {
                    AuthRateLimitPolicy.ChangePassword => Window($"{policy}|user|{userId}", settings.ChangePasswordPerUser),
                    AuthRateLimitPolicy.InvitationReissue => Window($"{policy}|user|{userId}", settings.InvitationReissuePerActor),
                    _ => RateLimitPartition.GetNoLimiter("none")
                };
            });

        /// <summary>Per target of the request (the employee whose invitation is re-issued), taken from the route.</summary>
        private static PartitionedRateLimiter<HttpContext> ByTarget(RateLimitingOptions settings)
            => PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!settings.Enabled
                    || PolicyOf(context) is not AuthRateLimitPolicy.InvitationReissue
                    || context.User.FindFirstValue(ClaimTypes.NameIdentifier) is null            // only authenticated callers count
                    || context.GetRouteValue("id") is not { } id)
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                return Window($"{AuthRateLimitPolicy.InvitationReissue}|target|{Convert.ToString(id, CultureInfo.InvariantCulture)}", settings.InvitationReissuePerTarget);
            });

        internal static RateLimitPartition<string> Window(string key, RateLimitRule rule)
            => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, rule.Permits),
                Window = rule.Window,
                QueueLimit = 0,                                  // never wait: reject immediately
                AutoReplenishment = true
            });

        /// <summary>
        /// The key of one client. IPv4 addresses are used as they are; IPv6 clients usually control a whole /64, so the prefix is used
        /// (otherwise one client could rotate through addresses to get a fresh budget each time).
        /// </summary>
        public static string AddressKey(IPAddress? address)
        {
            if (address is null)
            {
                return "unknown";
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return address.ToString();
            }

            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);

            return new IPAddress(bytes) + "/64";
        }

        private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
        {
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromSeconds(1);

            // Visible to operators (a burst of these is an attack or a mis-sized limit) without any address, token or e-mail.
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HRMS.RateLimiting")
                .LogWarning("A request was rejected by the rate limiter: {Method} {Path}.", context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);

            await WriteTooManyRequestsAsync(context.HttpContext, retryAfter, cancellationToken);
        }

        /// <summary>The single 429 shape, used by the rate limiter and by the exception middleware.</summary>
        public static Task WriteTooManyRequestsAsync(HttpContext context, TimeSpan retryAfter, CancellationToken cancellationToken = default)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

            return context.Response.WriteAsJsonAsync(new { message = TooManyRequestsException.StandardMessage }, cancellationToken);
        }
    }
}
