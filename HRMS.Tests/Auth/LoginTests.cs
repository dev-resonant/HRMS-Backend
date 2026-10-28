using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Application.Features.Auth.Commands.Login;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class LoginTests : IntegrationTestBase
{
    public LoginTests(PostgresFixture fx) : base(fx) { }

    [Fact]
    public async Task Valid_credentials_return_a_token_that_expires_in_about_eight_hours()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal(tenant.UserId, body.User.Id);
        Assert.Equal(tenant.CompanyId, body.User.CompanyId);
        Assert.Equal(tenant.Email, body.User.Email);

        // Uses the shipped appsettings.json value (480 minutes) - guards the specification's 8-hour requirement.
        Assert.InRange((body.ExpiresAt - DateTime.UtcNow.AddHours(8)).TotalMinutes, -2, 2);
    }

    [Fact]
    public async Task Login_creates_exactly_one_session_storing_only_the_token_hash_and_client_metadata()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var login = await ClientFor(tenant.Domain).LoginAsync(tenant.Email, tenant.Password);

        var session = Assert.Single(await TestData.GetSessionsAsync(Fx, tenant.UserId));
        Assert.Equal(TestData.HashToken(login.Token), session.TokenHash);
        Assert.NotEqual(login.Token, session.TokenHash);
        Assert.Equal(64, session.TokenHash.Length);                         // SHA-256 as hex
        Assert.DoesNotContain(login.Token, session.UserAgent ?? string.Empty);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, session.IpAddress);
        Assert.Equal(HttpHelpers.TestUserAgent, session.UserAgent);
        Assert.InRange((session.ExpiresAt - login.ExpiresAt).TotalSeconds, -1, 1);
    }

    [Fact]
    public async Task Login_records_LastLogin()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        await ClientFor(tenant.Domain).LoginAsync(tenant.Email, tenant.Password);

        await using var db = Fx.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
        Assert.NotNull(user.LastLogin);
        Assert.InRange((DateTime.UtcNow - user.LastLogin!.Value).TotalMinutes, -1, 1);
    }

    [Fact]
    public async Task A_user_whose_password_hash_was_created_with_cost_11_can_still_log_in()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var legacyHash = BCrypt.Net.BCrypt.HashPassword(tenant.Password, 11);      // as created before the work-factor change
        Assert.StartsWith("$2a$11$", legacyHash);

        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == tenant.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, legacyHash));
        }

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Newly_created_password_hashes_use_work_factor_12_and_log_in_successfully()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);       // TestData hashes with the production BCryptPasswordHasher

        await using (var db = Fx.CreateDbContext())
        {
            var hash = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId)).PasswordHash;
            Assert.StartsWith("$2a$12$", hash);
        }

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("upper")]
    [InlineData("mixed")]
    [InlineData("padded")]
    [InlineData("padded-and-upper")]
    public async Task Email_is_matched_case_insensitively_and_surrounding_whitespace_is_ignored(string variant)
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var email = variant switch
        {
            "upper" => tenant.Email.ToUpperInvariant(),
            "mixed" => char.ToUpperInvariant(tenant.Email[0]) + tenant.Email[1..],
            "padded" => $"  {tenant.Email} \t",
            _ => $" {tenant.Email.ToUpperInvariant()}  "
        };

        var response = await ClientFor(tenant.Domain).PostLoginAsync(email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_both_return_401_with_the_same_message_and_create_no_session()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);

        var unknown = await client.PostLoginAsync($"nobody-{Guid.NewGuid():N}@{tenant.Domain}", tenant.Password);
        var wrong = await client.PostLoginAsync(tenant.Email, tenant.Password + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await Message(unknown), await Message(wrong));
        Assert.Equal("Invalid email or password.", await Message(wrong));
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenant.UserId));
    }

    [Fact]
    public async Task Deactivated_user_with_the_correct_password_gets_403_and_no_session()
    {
        var tenant = await TestData.CreateTenantAsync(Fx, userActive: false);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Account is deactivated.", await Message(response));
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenant.UserId));
    }

    [Fact]
    public async Task Deactivated_user_with_a_wrong_password_gets_401_so_account_status_is_not_disclosed()
    {
        var tenant = await TestData.CreateTenantAsync(Fx, userActive: false);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("", "a-password", "Email")]
    [InlineData("not-an-email", "a-password", "Email")]
    [InlineData("user@example.test", "", "Password")]
    public async Task Invalid_input_returns_400_with_field_errors_and_writes_nothing(string email, string password, string expectedField)
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Validation failed.", json.RootElement.GetProperty("message").GetString());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _));
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenant.UserId));
    }

    [Fact]
    public async Task Over_long_password_is_rejected_with_400()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, new string('a', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await ClientFor(tenant.Domain).PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_host_returns_404()
    {
        var response = await ClientFor($"unknown-{Guid.NewGuid():N}.test").PostLoginAsync("a@b.test", "x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Inactive_company_returns_404_even_with_valid_credentials()
    {
        var tenant = await TestData.CreateTenantAsync(Fx, companyActive: false);

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenant.UserId));
    }

    [Fact]
    public async Task A_user_of_company_B_cannot_log_in_through_company_A()
    {
        var tenantA = await TestData.CreateTenantAsync(Fx);
        var tenantB = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenantA.Domain).PostLoginAsync(tenantB.Email, tenantB.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await TestData.GetSessionsAsync(Fx, tenantB.UserId));
    }

    private static async Task<string?> Message(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("message").GetString();
    }
}
