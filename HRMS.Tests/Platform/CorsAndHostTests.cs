using System.Net;
using HRMS.API.Extensions;
using HRMS.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace HRMS.Tests.Platform;

/// <summary>Configurable CORS (CorsSetup): origin resolution rules and the behaviour of a real host, plus the tenant-host contract.</summary>
[Collection(PostgresCollection.Name)]
public class CorsAndHostTests
{
    private readonly PostgresFixture _fx;

    public CorsAndHostTests(PostgresFixture fx) => _fx = fx;

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();

    // ================= origin resolution (pure) =================

    [Fact]
    public void Several_origins_from_an_array_or_one_comma_separated_value_are_normalized_and_deduplicated()
    {
        var fromArray = CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins:0", "https://App.Example.com/"), ("Cors:AllowedOrigins:1", "https://admin.example.com:8443")), "Production");
        var fromList = CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins", "https://app.example.com, https://app.example.com ,https://admin.example.com:8443")), "Production");

        Assert.Equal(new[] { "https://app.example.com", "https://admin.example.com:8443" }, fromArray);
        Assert.Equal(fromArray, fromList);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?x=1")]
    [InlineData("https://user@app.example.com")]
    [InlineData("ftp://app.example.com")]
    [InlineData("app.example.com")]
    public void Invalid_origins_are_refused_in_every_environment(string origin)
    {
        foreach (var environment in new[] { "Production", "Development", "Testing" })
        {
            Assert.Throws<InvalidOperationException>(() => CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins:0", origin)), environment));
        }
    }

    [Fact]
    public void Production_requires_configuration_and_https()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => CorsSetup.ResolveOrigins(Config(), "Production"));
        Assert.Contains("Cors:AllowedOrigins", missing.Message);
        Assert.Throws<InvalidOperationException>(() => CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins", " , ")), "Production"));
        Assert.Throws<InvalidOperationException>(() => CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins:0", "http://app.example.com")), "Production"));
        Assert.Throws<InvalidOperationException>(() => CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins:0", "http://localhost:5173")), "Production"));
    }

    [Fact]
    public void Development_defaults_to_the_local_vite_origin_unless_configured_and_other_environments_default_to_closed()
    {
        Assert.Equal(new[] { CorsSetup.DevelopmentDefaultOrigin }, CorsSetup.ResolveOrigins(Config(), "Development"));
        Assert.Equal(new[] { "http://localhost:3000" }, CorsSetup.ResolveOrigins(Config(("Cors:AllowedOrigins:0", "http://localhost:3000")), "Development"));
        Assert.Empty(CorsSetup.ResolveOrigins(Config(), "Testing"));
        Assert.Empty(CorsSetup.ResolveOrigins(Config(), "Staging"));
    }

    // ================= a real host =================

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/employees");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PUT");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,if-match,content-type");
        return request;
    }

    private HrmsWebApplicationFactory Factory(params string[] origins)
        => new(_fx, settings: origins.Select((o, i) => ($"Cors:AllowedOrigins:{i}", o)).ToDictionary(p => p.Item1, p => (string?)p.Item2));

    [Fact]
    public async Task Configured_origins_are_allowed_without_credentials_and_others_are_not()
    {
        using var factory = Factory("https://app.example.com", "https://admin.example.com");
        using var client = factory.CreateClient();

        foreach (var allowed in new[] { "https://app.example.com", "https://admin.example.com" })
        {
            var response = await client.SendAsync(Preflight(allowed));
            Assert.Equal(allowed, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }

        foreach (var rejected in new[] { "https://evil.example.com", "http://app.example.com", "https://app.example.com.evil.test", "http://localhost:5173" })
        {
            var response = await client.SendAsync(Preflight(rejected));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), rejected);
        }
    }

    [Fact]
    public async Task A_simple_request_from_an_allowed_origin_exposes_ETag_and_Retry_After()
    {
        using var factory = Factory("https://app.example.com");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.TryAddWithoutValidation("Origin", "https://app.example.com");

        var response = await client.SendAsync(request);

        Assert.Equal("https://app.example.com", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("ETag", exposed);
        Assert.Contains("Retry-After", exposed);
    }

    [Fact]
    public async Task Without_configuration_a_non_production_host_allows_no_origin_at_all()
    {
        using var factory = new HrmsWebApplicationFactory(_fx);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("http://localhost:5173"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Production_without_or_with_invalid_cors_configuration_refuses_to_start_before_touching_the_database()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var seed = new Dictionary<string, string?> { ["HRMS_TENANT_DOMAIN"] = "cors.test", ["HRMS_ADMIN_EMAIL"] = "a@cors.test", ["HRMS_ADMIN_PASSWORD"] = TestData.NewPassword() };

        foreach (var origin in new string?[] { null, "*", "http://app.example.com" })
        {
            var settings = new Dictionary<string, string?>(seed);
            if (origin is not null) settings["Cors:AllowedOrigins:0"] = origin;
            using var factory = new HrmsWebApplicationFactory(_fx, scratch: database, settings: settings, environmentName: "Production");

            var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("Cors:AllowedOrigins", failure.ToString());
        }

        Assert.Equal("0", (await database.QueryAsync("select count(*) from information_schema.tables where table_name = 'Companies'")).Single());   // nothing migrated or seeded
    }

    [Fact]
    public async Task Production_with_a_valid_https_origin_starts()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var settings = new Dictionary<string, string?>
        {
            ["HRMS_TENANT_DOMAIN"] = "cors.test", ["HRMS_ADMIN_EMAIL"] = "a@cors.test", ["HRMS_ADMIN_PASSWORD"] = TestData.NewPassword(),
            ["Cors:AllowedOrigins:0"] = "https://app.example.com"
        };
        using var factory = new HrmsWebApplicationFactory(_fx, scratch: database, settings: settings, environmentName: "Production");
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://app.example.com"));

        Assert.Equal("https://app.example.com", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    // ================= tenant host contract =================

    [Fact]
    public async Task The_tenant_is_the_request_host_and_a_trusted_proxy_cannot_change_it_with_forwarded_host_or_a_company_id()
    {
        var one = await TestData.CreateTenantAsync(_fx);
        var two = await TestData.CreateTenantAsync(_fx);
        using var factory = new HrmsWebApplicationFactory(_fx, settings: new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = HrmsWebApplicationFactory.TestRemoteIp          // the proxy is trusted for X-Forwarded-For/Proto only
        });
        var client = factory.ClientFor(one.Domain);
        var login = await client.LoginAsync(one.Email, one.Password);

        using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        forwarded.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.Token);
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-Host", two.Domain);
        forwarded.Headers.TryAddWithoutValidation("X-Company-Id", two.CompanyId.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(forwarded)).StatusCode);

        // Only the Host header selects the tenant: the same token against the other tenant's host is refused.
        var other = factory.ClientFor(two.Domain);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetMeAsync(login.Token)).StatusCode);

        var unknown = factory.ClientFor("unknown-tenant.test");
        Assert.Equal(HttpStatusCode.NotFound, (await unknown.PostLoginAsync(one.Email, one.Password)).StatusCode);
    }

    [Fact]
    public async Task A_trusted_network_range_that_contains_the_peer_is_honoured_and_one_that_does_not_is_ignored()
    {
        var tenant = await TestData.CreateCompanyAndRoleAsync(_fx);

        foreach (var network in new[] { "203.0.113.0/24", "10.0.0.0/8" })
        {
            using var factory = new HrmsWebApplicationFactory(_fx, settings: new Dictionary<string, string?>
            {
                ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownNetworks:0"] = network
            });
            var client = factory.ClientFor(tenant.Domain);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/set-password");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.77");
            request.Content = System.Net.Http.Json.JsonContent.Create(new { token = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), newPassword = "Correct-Horse-Battery-9!" });
            await client.SendAsync(request);
        }

        await using var db = _fx.CreateDbContext();
        var addresses = db.SecurityEvents.Where(e => e.CompanyId == tenant.CompanyId).OrderBy(e => e.CreatedAt).Select(e => e.IpAddress).ToList();
        Assert.Equal(new[] { "198.51.100.77", HrmsWebApplicationFactory.TestRemoteIp }, addresses);
    }
}
