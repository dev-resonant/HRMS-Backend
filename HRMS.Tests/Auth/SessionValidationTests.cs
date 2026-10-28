using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Application.Features.Auth.Queries.Me;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Auth;

/// <summary>JWT validation, server-side session enforcement, tenant mismatch and GET /api/auth/me.</summary>
[Collection(PostgresCollection.Name)]
public class SessionValidationTests : IntegrationTestBase
{
    public SessionValidationTests(PostgresFixture fx) : base(fx) { }

    private string MintToken(TestTenant tenant, string? secret = null, string? issuer = null, string? audience = null,
        DateTime? notBefore = null, DateTime? expires = null)
        => TestJwt.Create(secret ?? Fx.JwtSecret, issuer ?? Factory.Issuer, audience ?? Factory.Audience,
            tenant.UserId, tenant.CompanyId, tenant.Email, "Admin", notBefore, expires);

    [Fact]
    public async Task Me_with_a_login_token_returns_the_profile_and_permissions()
    {
        var tenant = await TestData.CreateTenantAsync(Fx, permissionsJson: "{\"employees\": [\"view_own\"]}");
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        var response = await client.GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponseDto>();
        Assert.NotNull(me);
        Assert.Equal(tenant.UserId, me!.Id);
        Assert.Equal(tenant.CompanyId, me.CompanyId);
        Assert.Equal(tenant.Email, me.Email);
        Assert.Equal("Admin", me.Role);                                  // the stable role key is the role's Name (legacy company role here)
        Assert.Equal("Administrator", me.RoleDisplayName);
        Assert.True(me.IsActive);
        Assert.Equal(new[] { "view_own" }, me.Permissions["employees"]);
    }

    [Fact]
    public async Task Me_without_a_token_returns_401()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).GetMeAsync(null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_a_malformed_token_returns_401()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).GetMeAsync("this-is-not-a-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_jwt_without_a_session_row_is_rejected()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = MintToken(tenant);                                   // correct signature, issuer, audience, lifetime

        var response = await ClientFor(tenant.Domain).GetMeAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_same_token_is_accepted_once_a_matching_session_exists()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = MintToken(tenant);
        await TestData.AddSessionAsync(Fx, tenant.UserId, token, DateTime.UtcNow.AddHours(1));

        var response = await ClientFor(tenant.Domain).GetMeAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_a_different_key_is_rejected_even_with_a_session()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = MintToken(tenant, secret: new string('z', 64));
        await TestData.AddSessionAsync(Fx, tenant.UserId, token, DateTime.UtcNow.AddHours(1));

        var response = await ClientFor(tenant.Domain).GetMeAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-issuer", null)]
    [InlineData(null, "wrong-audience")]
    public async Task A_token_with_the_wrong_issuer_or_audience_is_rejected_even_with_a_session(string? issuer, string? audience)
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = MintToken(tenant, issuer: issuer, audience: audience);
        await TestData.AddSessionAsync(Fx, tenant.UserId, token, DateTime.UtcNow.AddHours(1));

        var response = await ClientFor(tenant.Domain).GetMeAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_jwt_is_rejected_even_when_its_session_row_is_still_live()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var token = MintToken(tenant, notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1));
        await TestData.AddSessionAsync(Fx, tenant.UserId, token, DateTime.UtcNow.AddHours(1));

        var response = await ClientFor(tenant.Domain).GetMeAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_session_row_makes_a_cryptographically_valid_token_unusable()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(login.Token)).StatusCode);

        await using (var db = Fx.CreateDbContext())
        {
            await db.Sessions.Where(s => s.UserId == tenant.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        var response = await client.GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_session_makes_the_token_unusable()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        await using (var db = Fx.CreateDbContext())
        {
            await db.Sessions.Where(s => s.UserId == tenant.UserId).ExecuteDeleteAsync();
        }

        var response = await client.GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_company_A_is_rejected_with_403_on_company_Bs_host()
    {
        var tenantA = await TestData.CreateTenantAsync(Fx);
        var tenantB = await TestData.CreateTenantAsync(Fx);
        var login = await ClientFor(tenantA.Domain).LoginAsync(tenantA.Email, tenantA.Password);

        var response = await ClientFor(tenantB.Domain).GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Tenant access denied.", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Me_for_a_user_deactivated_after_login_returns_403()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == tenant.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var response = await client.GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Account is deactivated.", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Me_for_a_user_that_no_longer_exists_returns_401()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == tenant.UserId).ExecuteDeleteAsync();   // sessions cascade with the user
        }

        var response = await client.GetMeAsync(login.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
