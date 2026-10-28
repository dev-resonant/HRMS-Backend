using System.Net;
using System.Reflection;
using System.Text.Json;
using HRMS.API.Authorization;
using HRMS.API.Controllers;
using HRMS.Domain.Authorization;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Employees;

/// <summary>
/// POST /api/auth/change-password and first-login (is_first_login) enforcement. An account that still has to set its password can
/// log in but can do nothing except change its password, read /api/auth/me and log out.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FirstLoginAndChangePasswordTests : EmployeeApiTestBase
{
    private const string OldPassword = "Old-Password-Value-12!";

    public FirstLoginAndChangePasswordTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private Task<HttpResponseMessage> ChangeAsync(AuthzCompany company, string? token, string current, string next)
        => CallAsync(company, HttpMethod.Post, "/api/auth/change-password", token, new { currentPassword = current, newPassword = next });

    // ================= first-login enforcement =================

    [Fact]
    public async Task Login_succeeds_for_an_account_with_a_pending_password_change_and_says_so()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);

        var login = await Factory.ClientFor(company.Domain).PostLoginAsync(hr.Email, OldPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var user = (await JsonOf(login)).GetProperty("user");
        Assert.True(user.GetProperty("passwordChangeRequired").GetBoolean());
        Assert.Equal("hr_admin", user.GetProperty("role").GetString());                      // the role is unchanged; only the usage is restricted
    }

    [Fact]
    public async Task While_the_change_is_pending_every_protected_endpoint_except_me_change_password_and_logout_is_refused()
    {
        var (company, hr) = await SetupAsync();                    // an HR admin: has every employee permission
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        await SetFirstLoginAsync(hr.UserId, true);
        var before = await CountsAsync(company.CompanyId);

        var refused = new[]
        {
            await GetAsync(company, "/api/employees", hr.Token),
            await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token),
            await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId)),
            await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
                new { name = "x", departmentId = company.DepartmentId, managerId = (Guid?)null, designation = "x", joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null }),
            await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" }),
            await CallAsync(company, HttpMethod.Post, $"/api/employees/{target.EmployeeId}/invitation", hr.Token)
        };

        Assert.All(refused, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Contains("change your password", await refused[0].Content.ReadAsStringAsync());
        Assert.Equal(before, await CountsAsync(company.CompanyId));                          // nothing was read-modified or created

        var client = Factory.ClientFor(company.Domain);
        var me = await client.GetMeAsync(hr.Token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True((await JsonOf(me)).GetProperty("passwordChangeRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(hr.Token)).StatusCode);
    }

    [Fact]
    public async Task A_user_without_any_special_role_is_held_back_the_same_way()
    {
        var (company, _) = await SetupAsync();
        var employee = await EmployeeAsync(company);
        await SetFirstLoginAsync(employee.UserId, true);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, $"/api/employees/{employee.EmployeeId}", employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Factory.ClientFor(company.Domain).GetMeAsync(employee.Token)).StatusCode);
    }

    [Fact]
    public async Task Only_me_and_change_password_are_allowed_while_a_password_change_is_pending()
    {
        var allowed = typeof(AuthController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<SelfServiceAttribute>().Any(a => a.AllowWhilePasswordChangePending))
            .Select(m => m.Name)
            .OrderBy(n => n);

        Assert.Equal(new[] { nameof(AuthController.ChangePassword), nameof(AuthController.Me) }, allowed);

        // No permission-protected action can opt out: RequirePermission has no such switch.
        Assert.DoesNotContain(typeof(RequirePermissionAttribute).GetProperties(), p => p.Name.Contains("Pending", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    // ================= change password =================

    [Fact]
    public async Task Changing_the_password_clears_the_flag_keeps_this_login_and_ends_every_other_one()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);
        await TestData.AddSessionAsync(Fx, hr.UserId, "other-device-1", DateTime.UtcNow.AddHours(1));
        await TestData.AddSessionAsync(Fx, hr.UserId, "other-device-2", DateTime.UtcNow.AddHours(1));
        var bystander = await SeedAsync(company, "Bystander", "EMP-0050");
        var bystanderSessions = (await TestData.GetSessionsAsync(Fx, bystander.UserId)).Count;

        var response = await ChangeAsync(company, hr.Token, OldPassword, StrongPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = (await LoadAsync(hr.EmployeeId!.Value)).User;
        Assert.False(user.IsFirstLogin);
        Assert.True(new BCryptPasswordHasher().Verify(StrongPassword, user.PasswordHash));
        Assert.False(new BCryptPasswordHasher().Verify(OldPassword, user.PasswordHash));

        var remaining = await TestData.GetSessionsAsync(Fx, hr.UserId);
        Assert.Single(remaining);                                                               // only the session of THIS request survives
        Assert.Equal(TestData.HashToken(hr.Token), remaining[0].TokenHash);
        Assert.Equal(bystanderSessions, (await TestData.GetSessionsAsync(Fx, bystander.UserId)).Count);   // other accounts are untouched

        // The same token is no longer held back.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(company, "/api/employees", hr.Token)).StatusCode);

        // Old password is dead, new password logs in.
        var client = Factory.ClientFor(company.Domain);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync(hr.Email, OldPassword)).StatusCode);
        var login = await client.PostLoginAsync(hr.Email, StrongPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await JsonOf(login)).GetProperty("user").GetProperty("passwordChangeRequired").GetBoolean());
    }

    [Fact]
    public async Task The_change_is_audited_without_any_secret()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);
        await TestData.AddSessionAsync(Fx, hr.UserId, "other-device-1", DateTime.UtcNow.AddHours(1));

        var response = await ChangeAsync(company, hr.Token, OldPassword, StrongPassword);

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "auth.password_changed"));
        Assert.Equal("auth", audit.Module);
        Assert.Equal(hr.UserId, audit.TargetId);
        Assert.Equal(hr.UserId, audit.UserId);
        Assert.Equal(company.CompanyId, audit.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, audit.IpAddress);
        using var before = JsonDocument.Parse(audit.OldValue!);
        using var after = JsonDocument.Parse(audit.NewValue!);
        Assert.True(before.RootElement.GetProperty("firstLoginPending").GetBoolean());
        Assert.False(after.RootElement.GetProperty("firstLoginPending").GetBoolean());
        Assert.Equal(1, after.RootElement.GetProperty("otherSessionsRevoked").GetInt32());

        var stored = (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash;
        var everything = audit.OldValue + audit.NewValue + await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(OldPassword, everything);
        Assert.DoesNotContain(StrongPassword, everything);
        Assert.DoesNotContain(stored, everything);
        Assert.DoesNotContain(hr.Token, everything);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(OldPassword) || e.Message.Contains(StrongPassword) || e.Message.Contains(hr.Token));
    }

    [Fact]
    public async Task A_normal_password_change_works_for_an_account_with_no_pending_flag()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(company, hr.Token, OldPassword, StrongPassword)).StatusCode);

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "auth.password_changed"));
        using var before = JsonDocument.Parse(audit.OldValue!);
        Assert.False(before.RootElement.GetProperty("firstLoginPending").GetBoolean());
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);
        await TestData.AddSessionAsync(Fx, hr.UserId, "other-device-1", DateTime.UtcNow.AddHours(1));
        var hashBefore = (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash;

        var response = await ChangeAsync(company, hr.Token, "Not-The-Current-Pw-1!", StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CurrentPassword", await response.Content.ReadAsStringAsync());
        var user = (await LoadAsync(hr.EmployeeId!.Value)).User;
        Assert.Equal(hashBefore, user.PasswordHash);
        Assert.True(user.IsFirstLogin);
        Assert.Equal(2, (await TestData.GetSessionsAsync(Fx, hr.UserId)).Count);               // no session was touched
        Assert.Empty(await AuditAsync(company.CompanyId, "auth.password_changed"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("Old-Password-Value-12!")]                       // the same as the current one
    public async Task A_weak_or_unchanged_new_password_is_refused(string newPassword)
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);
        var hashBefore = (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash;

        var response = await ChangeAsync(company, hr.Token, OldPassword, newPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(hashBefore, (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash);
    }

    [Fact]
    public async Task The_email_address_is_refused_as_a_password()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);

        var response = await ChangeAsync(company, hr.Token, OldPassword, hr.Email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_authenticated_account_is_changed_and_unauthenticated_or_deactivated_callers_are_refused()
    {
        var (company, hr) = await SetupAsync();
        var other = await HrAsync(company);
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);
        await SetKnownPasswordAsync(other.UserId, OldPassword, firstLogin: false);
        var otherHash = (await LoadAsync(other.EmployeeId!.Value)).User.PasswordHash;

        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(company, null, OldPassword, StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(company, hr.Token, OldPassword, StrongPassword)).StatusCode);
        Assert.Equal(otherHash, (await LoadAsync(other.EmployeeId!.Value)).User.PasswordHash);   // no way to name another account

        Assert.Equal(HttpStatusCode.OK,
            (await CallAsync(company, HttpMethod.Put, $"/api/employees/{other.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(company, other.Token, OldPassword, StrongPassword)).StatusCode);   // its session is gone
    }

    [Fact]
    public async Task A_token_of_another_company_cannot_change_a_password_here()
    {
        var (company, hr) = await SetupAsync();
        var (_, foreignHr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);
        var hashBefore = (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash;

        var response = await ChangeAsync(company, foreignHr.Token, OldPassword, StrongPassword);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(hashBefore, (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash);
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_change_back_and_keeps_the_other_sessions()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: true);
        await TestData.AddSessionAsync(Fx, hr.UserId, "other-device-1", DateTime.UtcNow.AddHours(1));
        var hashBefore = (await LoadAsync(hr.EmployeeId!.Value)).User.PasswordHash;
        Switch.AuditStageFails = true;

        var response = await ChangeAsync(company, hr.Token, OldPassword, StrongPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var user = (await LoadAsync(hr.EmployeeId!.Value)).User;
        Assert.Equal(hashBefore, user.PasswordHash);
        Assert.True(user.IsFirstLogin);
        Assert.Equal(2, (await TestData.GetSessionsAsync(Fx, hr.UserId)).Count);               // the bulk DELETE was rolled back
        Assert.Empty(await AuditAsync(company.CompanyId, "auth.password_changed"));
    }

    // ================= policy =================

    [Theory]
    [InlineData("Correct-Horse-Battery-9!", true)]
    [InlineData("exactly12char", true)]
    [InlineData("elevenchars", false)]
    [InlineData("", false)]
    [InlineData("            ", false)]
    [InlineData("has a\ttab-and-is-long-enough", false)]
    public void The_password_policy_accepts_and_refuses_as_documented(string password, bool expected)
        => Assert.Equal(expected, HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(password));

    [Fact]
    public void The_policy_counts_bytes_not_characters_at_the_upper_limit()
    {
        Assert.True(HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(new string('a', 72)));
        Assert.False(HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(new string('a', 73)));
        Assert.True(HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(new string('é', 36)));      // 72 bytes
        Assert.False(HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(new string('é', 37)));     // 74 bytes
        Assert.False(HRMS.Application.Features.Auth.PasswordPolicy.IsAcceptable(null));
    }
}
