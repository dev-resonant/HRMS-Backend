using System.Net;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class LogoutTests : IntegrationTestBase
{
    public LogoutTests(PostgresFixture fx) : base(fx) { }

    [Fact]
    public async Task Logout_succeeds_deletes_the_session_and_the_token_stops_working()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(login.Token)).StatusCode);
        Assert.Single(await TestData.GetSessionsAsync(Fx, tenant.UserId));

        var logout = await client.LogoutAsync(login.Token);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenant.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetMeAsync(login.Token)).StatusCode);
    }

    [Fact]
    public async Task Logging_out_twice_with_the_same_token_returns_401_the_second_time()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(login.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LogoutAsync(login.Token)).StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_only_the_current_session()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);

        var first = await client.LoginAsync(tenant.Email, tenant.Password);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));   // the JWT has no jti: logins in the same second would yield identical tokens
        var second = await client.LoginAsync(tenant.Email, tenant.Password);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(2, (await TestData.GetSessionsAsync(Fx, tenant.UserId)).Count);

        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(first.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetMeAsync(first.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(second.Token)).StatusCode);
        Assert.Single(await TestData.GetSessionsAsync(Fx, tenant.UserId));
    }

    [Fact]
    public async Task Logout_does_not_touch_another_users_session()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var other = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var otherClient = ClientFor(other.Domain);

        var login = await client.LoginAsync(tenant.Email, tenant.Password);
        var otherLogin = await otherClient.LoginAsync(other.Email, other.Password);

        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(login.Token)).StatusCode);

        Assert.Single(await TestData.GetSessionsAsync(Fx, other.UserId));
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetMeAsync(otherLogin.Token)).StatusCode);
    }

    [Fact]
    public async Task Logout_without_a_token_returns_401()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).LogoutAsync(null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_with_a_token_that_has_no_session_returns_401()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = TestJwt.Create(Fx.JwtSecret, Factory.Issuer, Factory.Audience, tenant.UserId, tenant.CompanyId, tenant.Email);

        var response = await ClientFor(tenant.Domain).LogoutAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
