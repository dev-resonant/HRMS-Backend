using System.Net;
using HRMS.API.Authorization;
using HRMS.Domain.Authorization;
using HRMS.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace HRMS.Tests.Authorization;

/// <summary>
/// Regression tests for the bypass found in the 2026-10-21 verification: the global filter returned early whenever
/// [AllowAnonymous] was present in the endpoint metadata, so a controller-level [AllowAnonymous] silently disabled an
/// action's [RequirePermission]. Conflicting declarations must now always be denied.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthorizationBypassRegressionTests : IDisposable
{
    private readonly PostgresFixture _fx;
    private readonly HrmsWebApplicationFactory _factory;

    public AuthorizationBypassRegressionTests(PostgresFixture fx)
    {
        _fx = fx;
        _factory = new HrmsWebApplicationFactory(fx, ProbeHost.AddProbeController);
        _factory.AssertUsesFixtureDatabase();
    }

    public void Dispose() => _factory.Dispose();

    private Task<HttpResponseMessage> Get(AuthzCompany company, string url, string? token)
        => _factory.ClientFor(company.Domain).SendAsync(HttpMethod.Get, url, token);

    private async Task<(AuthzCompany Company, AuthzUser Employee, AuthzUser Manager)> Setup()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var employee = await AuthzTestData.AddSystemUserAsync(_fx, _factory, company, SystemRoleKeys.Employee);   // lacks employees.view_team
        var manager = await AuthzTestData.AddSystemUserAsync(_fx, _factory, company, SystemRoleKeys.Manager);     // holds employees.view_team
        return (company, employee, manager);
    }

    // ---- controller-level [AllowAnonymous] + action-level [RequirePermission] (the vulnerability) ----

    [Fact]
    public async Task A_permission_protected_action_is_not_reachable_anonymously_when_its_controller_is_AllowAnonymous()
    {
        var (company, _, _) = await Setup();

        var response = await Get(company, "/api/probe-anon-class/permission", null);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);                  // the old filter returned 200 here
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("reached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_user_lacking_the_permission_is_denied_even_when_the_controller_is_AllowAnonymous()
    {
        var (company, employee, _) = await Setup();

        var response = await Get(company, "/api/probe-anon-class/permission", employee.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);              // the old filter returned 200 here
        Assert.DoesNotContain("reached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Conflicting_declarations_are_denied_even_for_a_user_who_holds_the_permission()
    {
        var (company, _, manager) = await Setup();

        // Not a bypass, but a misconfigured endpoint must not become reachable by accident: deny, do not guess the intent.
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-anon-class/permission", manager.Token)).StatusCode);
    }

    [Fact]
    public async Task A_self_service_action_is_not_reachable_anonymously_when_its_controller_is_AllowAnonymous()
    {
        var (company, employee, _) = await Setup();

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-anon-class/self", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-anon-class/self", employee.Token)).StatusCode);
    }

    [Fact]
    public async Task An_undecorated_action_in_an_AllowAnonymous_controller_is_still_anonymous()
    {
        var (company, _, _) = await Setup();

        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe-anon-class/plain", null)).StatusCode);
    }

    // ---- controller-level permission + action-level [AllowAnonymous] ----

    [Fact]
    public async Task Action_level_AllowAnonymous_does_not_open_a_permission_protected_controller()
    {
        var (company, employee, manager) = await Setup();

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-permission-class/anonymous", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-permission-class/anonymous", employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-permission-class/anonymous", manager.Token)).StatusCode);
    }

    [Fact]
    public async Task A_controller_level_permission_still_protects_normal_actions_correctly()
    {
        var (company, employee, manager) = await Setup();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(company, "/api/probe-permission-class/normal", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe-permission-class/normal", employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe-permission-class/normal", manager.Token)).StatusCode);
    }

    // ---- other contradictory combinations ----

    [Theory]
    [InlineData("/api/probe-conflict/permission-and-self")]
    [InlineData("/api/probe-conflict/bare-authorize-and-anonymous")]
    public async Task Other_contradictory_declarations_are_denied_for_everyone(string url)
    {
        var (company, employee, manager) = await Setup();

        // Anonymous callers are denied either by the authentication layer (401, when [Authorize] data is present and no
        // [AllowAnonymous] disables it) or by the filter (403); never 200.
        var anonymous = (await Get(company, url, null)).StatusCode;
        Assert.True(anonymous is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"anonymous got {anonymous}");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, url, employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, url, manager.Token)).StatusCode);
    }

    // ---- the intended anonymous and self-service endpoints are unchanged ----

    [Fact]
    public async Task Health_login_logout_and_me_keep_their_intended_behavior()
    {
        var (company, employee, _) = await Setup();
        var client = _factory.ClientFor(company.Domain);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, "/healthz", null)).StatusCode);                 // anonymous
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync("nobody@" + company.Domain, "wrong-password-1")).StatusCode);   // anonymous, reaches the handler
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetMeAsync(null)).StatusCode);                                  // self-service needs a token
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LogoutAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(employee.Token)).StatusCode);
    }
}

/// <summary>The classification rule used by both the runtime filter and the convention tests.</summary>
public class AuthorizationDeclarationTests
{
    private static readonly AllowAnonymousAttribute Anon = new();
    private static readonly RequirePermissionAttribute Perm = new("employees", "view_own");
    private static readonly RequirePermissionAttribute Perm2 = new("employees", "view_team");
    private static readonly SelfServiceAttribute Self = new();
    private static readonly AuthorizeAttribute Bare = new();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classification(object[] metadata, DeclarationKind expected)
        => Assert.Equal(expected, AuthorizationDeclaration.Classify(metadata));

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { new object[] { }, DeclarationKind.Undeclared };
        yield return new object[] { new object[] { Bare }, DeclarationKind.Undeclared };
        yield return new object[] { new object[] { Anon }, DeclarationKind.Anonymous };
        yield return new object[] { new object[] { Perm }, DeclarationKind.Protected };
        yield return new object[] { new object[] { Perm, Perm2 }, DeclarationKind.Protected };        // several permissions: all required
        yield return new object[] { new object[] { Self }, DeclarationKind.Protected };
        yield return new object[] { new object[] { Perm, Bare }, DeclarationKind.Protected };

        // every combination with [AllowAnonymous] next to a requirement is a conflict, in either order
        yield return new object[] { new object[] { Anon, Perm }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Perm, Anon }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Anon, Self }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Self, Anon }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Anon, Bare }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Anon, Perm, Self }, DeclarationKind.Conflict };
        yield return new object[] { new object[] { Perm, Self }, DeclarationKind.Conflict };
    }
}
