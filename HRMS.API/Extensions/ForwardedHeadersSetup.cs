using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace HRMS.API.Extensions
{
    /// <summary>
    /// Client-address policy. By default NO forwarded header is trusted: the client address is the TCP peer, so a caller can never choose
    /// the address that is audited, stored in sessions or used as a rate-limit key by sending an X-Forwarded-For header.
    /// <para>
    /// Behind a reverse proxy or load balancer the peer is the proxy, not the user (every user then shares one rate-limit budget), so
    /// the deployment must opt in with <c>ForwardedHeaders:Enabled = true</c> and name the proxies it trusts: <c>KnownProxies</c>
    /// (addresses) and/or <c>KnownNetworks</c> (CIDR ranges). Only X-Forwarded-For / X-Forwarded-Proto sent BY such a peer are honoured,
    /// and <c>ForwardLimit</c> (default 1) bounds how many entries are read from the right, so entries a client prepended are ignored.
    /// Enabling it without naming any proxy fails at startup instead of silently trusting everybody. The explicit escape hatch
    /// <c>TrustAllProxies = true</c> is only correct when the application can be reached exclusively through a proxy that overwrites or
    /// appends X-Forwarded-For (many platform load balancers do); with a directly reachable port it lets any caller spoof its address.
    /// </para>
    /// </summary>
    public static class ForwardedHeadersSetup
    {
        public const string SectionName = "ForwardedHeaders";

        public static IServiceCollection ConfigureForwardedHeaders(this IServiceCollection services)
        {
            // Evaluated when the middleware is created at start-up (so a bad setting stops the application from starting), from the
            // built host's configuration.
            services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>(Apply);

            return services;
        }

        private static void Apply(ForwardedHeadersOptions options, IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);

            if (!section.GetValue("Enabled", false))
            {
                options.ForwardedHeaders = ForwardedHeaders.None;                // the middleware then passes every request straight through
                return;
            }

            var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
            var trustAll = section.GetValue("TrustAllProxies", false);

            if (proxies.Length == 0 && networks.Length == 0 && !trustAll)
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders:Enabled is true but no trusted proxy is configured. List ForwardedHeaders:KnownProxies and/or " +
                    "ForwardedHeaders:KnownNetworks, or set ForwardedHeaders:TrustAllProxies explicitly (see the deployment notes).");
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = section.GetValue("ForwardLimit", 1);

            // Replace the framework default (loopback only) with exactly what was configured.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            if (trustAll)
            {
                // With no known proxy or network the middleware does not check the peer at all.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
            }
        }
    }
}
