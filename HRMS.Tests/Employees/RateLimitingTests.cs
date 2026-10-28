using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using HRMS.API.Controllers;
using HRMS.API.RateLimiting;
using HRMS.Application.Common.Exceptions;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HRMS.Tests.Employees;

/// <summary>Rate limiting of login, set-password, change-password and invitation re-issue: real pipeline, real database.</summary>
[Collection(PostgresCollection.Name)]
public class RateLimitingTests : EmployeeApiTestBase
{
    private const string UserPassword = "User-Password-Value-3!";

    public RateLimitingTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private Task<HttpResponseMessage> LoginAsync(AuthzCompany company, string email, string password = "wrong-password-1", params (string, string)[] headers)
        => CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email, password }, headers);

    private static async Task AssertStandardRejectionAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, 3600);                                      // whole seconds
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TooManyRequestsException.StandardMessage, body.RootElement.GetProperty("message").GetString());
        Assert.Single(body.RootElement.EnumerateObject());                                   // nothing else is disclosed
    }

    // ================= login =================

    [Fact]
    public async Task Login_attempts_from_one_address_are_limited_whichever_accounts_they_name()
    {
        var (company, _) = await SetupAsync();
        Limits.LoginPerIp = new RateLimitRule(3, 60);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(company, $"nobody{i}@example.test")).StatusCode);
        }

        await AssertStandardRejectionAsync(await LoginAsync(company, "nobody-else@example.test"));
    }

    [Fact]
    public async Task The_account_budget_is_identical_for_accounts_that_exist_and_ones_that_do_not()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, UserPassword, firstLogin: false);
        Limits.LoginPerAccountAndIp = new RateLimitRule(2, 300);

        var existing = new List<HttpStatusCode>();
        var unknown = new List<HttpStatusCode>();
        HttpResponseMessage lastExisting = null!, lastUnknown = null!;

        for (var i = 0; i < 3; i++)
        {
            lastExisting = await LoginAsync(company, hr.Email);
            lastUnknown = await LoginAsync(company, "does-not-exist@example.test");
            existing.Add(lastExisting.StatusCode);
            unknown.Add(lastUnknown.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }, existing);
        Assert.Equal(existing, unknown);                                                      // no oracle for account existence
        Assert.Equal(await lastExisting.Content.ReadAsStringAsync(), await lastUnknown.Content.ReadAsStringAsync());
        await AssertStandardRejectionAsync(lastExisting);
        await AssertStandardRejectionAsync(lastUnknown);
    }

    [Fact]
    public async Task One_exhausted_account_does_not_block_other_accounts_and_the_e_mail_spelling_makes_no_difference()
    {
        var (company, hr) = await SetupAsync();
        Limits.LoginPerAccountAndIp = new RateLimitRule(2, 300);

        await LoginAsync(company, hr.Email);
        await LoginAsync(company, "  " + hr.Email.ToUpperInvariant() + " ");                // same account after normalisation
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(company, hr.Email)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(company, "someone-else@example.test")).StatusCode);
    }

    [Fact]
    public async Task The_same_e_mail_in_another_tenant_has_its_own_budget()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        Limits.LoginPerAccountAndIp = new RateLimitRule(1, 300);

        await LoginAsync(company, hr.Email);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(company, hr.Email)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(other, hr.Email)).StatusCode);
    }

    [Fact]
    public async Task A_limit_ends_by_itself_so_nobody_is_locked_out_permanently()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, UserPassword, firstLogin: false);
        await LoginAsync(company, "warm-up@example.test");                                      // JIT/cold-start cost must not eat the window
        Limits.LoginPerAccountAndIp = new RateLimitRule(1, 3);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(company, hr.Email)).StatusCode);
        var blocked = await LoginAsync(company, hr.Email, UserPassword);                       // even the right password waits...
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(int.Parse(blocked.Headers.GetValues("Retry-After").Single())) + TimeSpan.FromMilliseconds(300));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(company, hr.Email, UserPassword)).StatusCode);   // ...and then works
    }

    [Fact]
    public async Task Limits_can_be_switched_off_by_configuration()
    {
        var (company, _) = await SetupAsync();
        Limits.Enabled = false;
        Limits.LoginPerIp = new RateLimitRule(1, 60);
        Limits.LoginPerAccountAndIp = new RateLimitRule(1, 60);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(company, "same@example.test")).StatusCode);
        }
    }

    // ================= set-password =================

    [Fact]
    public async Task One_token_can_be_submitted_only_so_often_and_other_tokens_are_unaffected()
    {
        var (company, _) = await SetupAsync();
        Limits.SetPasswordPerToken = new RateLimitRule(2, 900);
        var junk = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken();

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, junk, StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, junk, StrongPassword)).StatusCode);
        await AssertStandardRejectionAsync(await SetPasswordAsync(company, junk, StrongPassword));

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), StrongPassword)).StatusCode);
        Assert.Equal(2, (await SecurityEventsAsync(company.CompanyId)).Count - 1);            // the rejected third attempt reached nothing: no event, no database work
    }

    [Fact]
    public async Task Set_password_attempts_from_one_address_are_limited()
    {
        var (company, _) = await SetupAsync();
        Limits.SetPasswordPerIp = new RateLimitRule(3, 60);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), StrongPassword)).StatusCode);
        }

        await AssertStandardRejectionAsync(await SetPasswordAsync(company, HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), StrongPassword));
        Assert.Equal(3, (await SecurityEventsAsync(company.CompanyId)).Count);
    }

    [Fact]
    public async Task Junk_tokens_cannot_use_up_the_budget_of_the_real_token()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Limits.SetPasswordPerToken = new RateLimitRule(2, 900);

        for (var i = 0; i < 6; i++)
        {
            await SetPasswordAsync(company, HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), StrongPassword);   // an attacker without the token
        }

        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    // ================= change-password =================

    [Fact]
    public async Task Change_password_attempts_are_limited_per_authenticated_user()
    {
        var (company, hr) = await SetupAsync();
        var manager = await ManagerAsync(company);
        await SetKnownPasswordAsync(hr.UserId, UserPassword, firstLogin: false);
        await SetKnownPasswordAsync(manager.UserId, UserPassword, firstLogin: false);
        Limits.ChangePasswordPerUser = new RateLimitRule(2, 900);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", hr.Token,
                new { currentPassword = "wrong-guess-" + i, newPassword = StrongPassword })).StatusCode);
        }

        var blocked = await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", hr.Token, new { currentPassword = UserPassword, newPassword = StrongPassword });
        await AssertStandardRejectionAsync(blocked);
        var events = await SecurityEventsAsync(company.CompanyId, "auth.change_password_failed");
        Assert.Equal(2, events.Count);                                                          // the two real attempts; nothing for the rejected one
        Assert.All(events, e => Assert.Equal(hr.UserId, e.UserId));

        // Another user is unaffected, and so is an unauthenticated caller (still just unauthorised).
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", manager.Token,
            new { currentPassword = UserPassword, newPassword = StrongPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", null,
            new { currentPassword = "x", newPassword = StrongPassword })).StatusCode);
    }

    // ================= invitation re-issue =================

    [Fact]
    public async Task Re_issuing_for_one_employee_is_limited_so_a_mailbox_cannot_be_flooded()
    {
        var (company, hr) = await SetupAsync();
        var first = await CreateViaApiAsync(company, hr);
        var second = await CreateViaApiAsync(company, hr);
        Limits.InvitationReissuePerTarget = new RateLimitRule(2, 3600);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{first.EmployeeId}/invitation", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{first.EmployeeId}/invitation", hr.Token)).StatusCode);
        var sentBefore = Sender.Sent.Count;

        await AssertStandardRejectionAsync(await CallAsync(company, HttpMethod.Post, $"/api/employees/{first.EmployeeId}/invitation", hr.Token));
        Assert.Equal(sentBefore, Sender.Sent.Count);                                          // nothing was sent, nothing was revoked

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{second.EmployeeId}/invitation", hr.Token)).StatusCode);
    }

    [Fact]
    public async Task Re_issuing_is_limited_per_administrator()
    {
        var (company, hr) = await SetupAsync();
        var targets = new[] { await CreateViaApiAsync(company, hr), await CreateViaApiAsync(company, hr), await CreateViaApiAsync(company, hr) };
        Limits.InvitationReissuePerActor = new RateLimitRule(2, 3600);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{targets[0].EmployeeId}/invitation", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{targets[1].EmployeeId}/invitation", hr.Token)).StatusCode);

        await AssertStandardRejectionAsync(await CallAsync(company, HttpMethod.Post, $"/api/employees/{targets[2].EmployeeId}/invitation", hr.Token));
    }

    [Fact]
    public async Task Unauthenticated_re_issue_requests_are_refused_as_unauthorised_not_counted_against_anyone()
    {
        var (company, hr) = await SetupAsync();
        var target = await CreateViaApiAsync(company, hr);
        Limits.InvitationReissuePerActor = new RateLimitRule(1, 3600);
        Limits.InvitationReissuePerTarget = new RateLimitRule(1, 3600);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{target.EmployeeId}/invitation", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{target.EmployeeId}/invitation", hr.Token)).StatusCode);   // the real administrator is not locked out
    }

    [Fact]
    public async Task Every_rejection_is_logged_as_a_warning_without_any_address_token_or_e_mail()
    {
        var (company, hr) = await SetupAsync();
        Limits.LoginPerIp = new RateLimitRule(1, 60);
        Limits.SetPasswordPerToken = new RateLimitRule(1, 900);
        var junk = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken();

        await LoginAsync(company, hr.Email);
        await LoginAsync(company, hr.Email);                                                   // rejected by the middleware limiter
        await SetPasswordAsync(company, junk, StrongPassword);
        await SetPasswordAsync(company, junk, StrongPassword);                                 // rejected by the attempt limiter

        var warnings = Logs.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("rejected by")).Select(e => e.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("rate limiter") && w.Contains("/api/auth/login"));
        Assert.Contains(warnings, w => w.Contains("attempt limit") && w.Contains("/api/auth/set-password"));
        Assert.All(warnings, w =>
        {
            Assert.DoesNotContain(HrmsWebApplicationFactory.TestRemoteIp, w);
            Assert.DoesNotContain(hr.Email, w);
            Assert.DoesNotContain(junk, w);
            Assert.DoesNotContain(StrongPassword, w);
        });
    }

    // ================= declaration and helpers =================

    [Fact]
    public void Exactly_the_credential_endpoints_declare_a_rate_limit_policy()
    {
        var declared = typeof(AuthController).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Action: $"{m.DeclaringType!.Name}.{m.Name}", Policy: m.GetCustomAttribute<AuthRateLimitAttribute>()?.Policy))
            .Where(x => x.Policy is not null)
            .OrderBy(x => x.Action)
            .ToList();

        Assert.Equal(new (string, AuthRateLimitPolicy?)[]
        {
            ("AuthController.ChangePassword", AuthRateLimitPolicy.ChangePassword),
            ("AuthController.Login", AuthRateLimitPolicy.Login),
            ("AuthController.SetPassword", AuthRateLimitPolicy.SetPassword),
            ("EmployeesController.IssueInvitation", AuthRateLimitPolicy.InvitationReissue)
        }, declared);
    }

    [Fact]
    public void IPv6_clients_share_their_64_bit_prefix_and_mapped_IPv4_addresses_are_unwrapped()
    {
        Assert.Equal("198.51.100.9", RateLimitingSetup.AddressKey(IPAddress.Parse("198.51.100.9")));
        Assert.Equal("198.51.100.9", RateLimitingSetup.AddressKey(IPAddress.Parse("::ffff:198.51.100.9")));
        Assert.Equal(RateLimitingSetup.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")), RateLimitingSetup.AddressKey(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.NotEqual(RateLimitingSetup.AddressKey(IPAddress.Parse("2001:db8:1:2::1")), RateLimitingSetup.AddressKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("unknown", RateLimitingSetup.AddressKey(null));
    }
}

/// <summary>Limits come from configuration (appsettings section "RateLimiting"), with defaults for what is not set.</summary>
[Collection(PostgresCollection.Name)]
public class RateLimitingConfigurationTests : EmployeeApiTestBase
{
    public RateLimitingConfigurationTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?>
    {
        ["RateLimiting:LoginPerIp:Permits"] = "2",
        ["RateLimiting:LoginPerIp:WindowSeconds"] = "120",
        ["RateLimiting:SetPasswordPerToken:Permits"] = "7"
    })
    {
    }

    [Fact]
    public async Task Configured_values_are_applied_and_unset_ones_keep_their_defaults()
    {
        Assert.Equal(2, Limits.LoginPerIp.Permits);
        Assert.Equal(120, Limits.LoginPerIp.WindowSeconds);
        Assert.Equal(7, Limits.SetPasswordPerToken.Permits);
        Assert.Equal(900, Limits.SetPasswordPerToken.WindowSeconds);                          // default window kept
        Assert.True(Limits.Enabled);

        var company = await NewCompanyAsync();
        var email = "cfg@example.test";
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email, password = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email = "b@example.test", password = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email = "c@example.test", password = "x" })).StatusCode);
    }

    [Fact]
    public void The_shipped_defaults_are_sensible_and_documented()
    {
        var defaults = new RateLimitingOptions();

        Assert.True(defaults.Enabled);
        Assert.Equal((20, 60), (defaults.LoginPerIp.Permits, defaults.LoginPerIp.WindowSeconds));
        Assert.Equal((10, 300), (defaults.LoginPerAccountAndIp.Permits, defaults.LoginPerAccountAndIp.WindowSeconds));
        Assert.Equal((10, 60), (defaults.SetPasswordPerIp.Permits, defaults.SetPasswordPerIp.WindowSeconds));
        Assert.Equal((10, 900), (defaults.SetPasswordPerToken.Permits, defaults.SetPasswordPerToken.WindowSeconds));
        Assert.Equal((30, 60), (defaults.ChangePasswordPerIp.Permits, defaults.ChangePasswordPerIp.WindowSeconds));
        Assert.Equal((10, 900), (defaults.ChangePasswordPerUser.Permits, defaults.ChangePasswordPerUser.WindowSeconds));
        Assert.Equal((5, 3600), (defaults.InvitationReissuePerTarget.Permits, defaults.InvitationReissuePerTarget.WindowSeconds));
    }
}

/// <summary>The client address is the TCP peer unless the deployment names the proxies it trusts.</summary>
[Collection(PostgresCollection.Name)]
public class TrustedProxyTests : EmployeeApiTestBase
{
    private const string Attacker = "198.51.100.20";
    private const string Victim = "198.51.100.30";

    public TrustedProxyTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?>
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:KnownProxies:0"] = HrmsWebApplicationFactory.TestRemoteIp          // the peer of every test request
    })
    {
    }

    private Task<HttpResponseMessage> LoginFromAsync(AuthzCompany company, string address, string email)
        => CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email, password = "wrong-password-1" }, ("X-Forwarded-For", address));

    [Fact]
    public async Task The_forwarded_address_of_a_trusted_proxy_is_what_is_recorded()
    {
        var company = await NewCompanyAsync();

        await CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null,
            new { token = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), newPassword = StrongPassword }, ("X-Forwarded-For", Attacker));

        Assert.Equal(Attacker, Assert.Single(await SecurityEventsAsync(company.CompanyId)).IpAddress);
    }

    [Fact]
    public async Task Entries_a_client_put_in_front_of_the_proxys_own_are_ignored()
    {
        var company = await NewCompanyAsync();

        await CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null,
            new { token = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), newPassword = StrongPassword },
            ("X-Forwarded-For", $"6.6.6.6, {Attacker}"));                                     // the proxy appended the real client address last

        Assert.Equal(Attacker, Assert.Single(await SecurityEventsAsync(company.CompanyId)).IpAddress);
    }

    [Fact]
    public async Task An_attacker_exhausting_an_account_budget_cannot_lock_the_real_user_out()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SetKnownPasswordAsync(hr.UserId, "User-Password-Value-3!", firstLogin: false);
        Limits.LoginPerAccountAndIp = new RateLimitRule(2, 300);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFromAsync(company, Attacker, hr.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFromAsync(company, Attacker, hr.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginFromAsync(company, Attacker, hr.Email)).StatusCode);       // the attacker is stopped...

        var legitimate = await CallAsync(company, HttpMethod.Post, "/api/auth/login", null,
            new { email = hr.Email, password = "User-Password-Value-3!" }, ("X-Forwarded-For", Victim));
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);                                                              // ...the victim is not
    }

    [Fact]
    public async Task Each_address_has_its_own_budget()
    {
        var company = await NewCompanyAsync();
        Limits.LoginPerIp = new RateLimitRule(2, 60);

        await LoginFromAsync(company, Attacker, "a@example.test");
        await LoginFromAsync(company, Attacker, "b@example.test");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginFromAsync(company, Attacker, "c@example.test")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFromAsync(company, Victim, "c@example.test")).StatusCode);
    }
}

/// <summary>A forwarded header from a peer that is not a listed proxy changes nothing, even when forwarding is enabled.</summary>
[Collection(PostgresCollection.Name)]
public class UntrustedPeerTests : EmployeeApiTestBase
{
    public UntrustedPeerTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?>
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.55",                                    // not the peer (203.0.113.7)
        ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8"
    })
    {
    }

    [Fact]
    public async Task The_header_is_ignored_for_the_record_and_for_the_budget()
    {
        var company = await NewCompanyAsync();
        Limits.LoginPerIp = new RateLimitRule(2, 60);

        for (var i = 0; i < 2; i++)
        {
            await CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email = $"u{i}@example.test", password = "x" }, ("X-Forwarded-For", $"198.51.100.{i + 1}"));
        }

        // Rotating the claimed address does not buy a fresh budget.
        var third = await CallAsync(company, HttpMethod.Post, "/api/auth/login", null, new { email = "u3@example.test", password = "x" }, ("X-Forwarded-For", "198.51.100.99"));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        Limits.LoginPerIp = new RateLimitRule(1000, 60);
        await CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null,
            new { token = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), newPassword = StrongPassword }, ("X-Forwarded-For", "198.51.100.5"));
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, Assert.Single(await SecurityEventsAsync(company.CompanyId)).IpAddress);
    }
}

/// <summary>Start-up validation of the forwarded-header settings.</summary>
[Collection(PostgresCollection.Name)]
public class ForwardedHeadersStartupTests
{
    private readonly PostgresFixture _fx;

    public ForwardedHeadersStartupTests(PostgresFixture fx) => _fx = fx;

    private HrmsWebApplicationFactory FactoryWith(params (string Key, string Value)[] settings)
        => new(_fx, settings: settings.ToDictionary(s => s.Key, s => (string?)s.Value));

    [Fact]
    public void Enabling_forwarded_headers_without_naming_any_proxy_refuses_to_start()
    {
        using var factory = FactoryWith(("ForwardedHeaders:Enabled", "true"));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("no trusted proxy", failure.ToString());
    }

    [Fact]
    public void A_malformed_proxy_address_refuses_to_start_instead_of_being_ignored()
    {
        using var factory = FactoryWith(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies:0", "not-an-address"));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Trusting_every_proxy_must_be_asked_for_explicitly_and_then_honours_the_header()
    {
        using var factory = FactoryWith(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:TrustAllProxies", "true"));
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var client = factory.ClientFor(company.Domain);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/set-password");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.44");
        request.Content = System.Net.Http.Json.JsonContent.Create(new { token = HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken(), newPassword = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);

        await using var db = _fx.CreateDbContext();
        Assert.Equal("198.51.100.44", await db.SecurityEvents.Where(e => e.CompanyId == company.CompanyId).Select(e => e.IpAddress).SingleAsync());
    }

    [Fact]
    public void By_default_forwarded_headers_are_not_enabled_at_all()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(FindApiDirectory(), "appsettings.json")).Build();

        Assert.False(shipped.GetValue("ForwardedHeaders:Enabled", false));
    }

    private static string FindApiDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var api = Path.Combine(directory.FullName, "HRMS.API");

            if (File.Exists(Path.Combine(api, "HRMS.API.csproj")))
            {
                return api;
            }
        }

        throw new InvalidOperationException("HRMS.API not found.");
    }
}
