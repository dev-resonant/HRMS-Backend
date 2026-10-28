using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Authorization;

/// <summary>
/// End-to-end authorization behaviour against the real pipeline: permission attributes, deny-by-default, per-request
/// permission loading (D5), own/team/company scopes (D1), company isolation and fail-closed identity handling.
/// Uses the test-only <see cref="AuthzProbeController"/>; the production API has no such endpoints.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthorizationFoundationTests : IDisposable
{
    private readonly PostgresFixture _fx;
    private readonly HrmsWebApplicationFactory _factory;

    public AuthorizationFoundationTests(PostgresFixture fx)
    {
        _fx = fx;
        _factory = new HrmsWebApplicationFactory(fx, ProbeHost.AddProbeController);
        _factory.AssertUsesFixtureDatabase();
    }

    public void Dispose() => _factory.Dispose();

    private Task<AuthzUser> SystemUser(AuthzCompany company, string key, bool withEmployee = true, Guid? manager = null, bool active = true)
        => AuthzTestData.AddSystemUserAsync(_fx, _factory, company, key, withEmployee, manager, active);

    private Task<HttpResponseMessage> Get(AuthzCompany company, string url, string? token)
        => _factory.ClientFor(company.Domain).SendAsync(HttpMethod.Get, url, token);

    private static async Task<List<Guid>> Ids(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Guid>>())!;
    }

    // ---------- employee ----------

    [Fact]
    public async Task Employee_receives_403_from_the_list_endpoint()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var employee = await SystemUser(company, SystemRoleKeys.Employee);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", employee.Token)).StatusCode);
    }

    [Fact]
    public async Task Employee_can_read_and_edit_only_their_own_record()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var me = await SystemUser(company, SystemRoleKeys.Employee);
        var other = await SystemUser(company, SystemRoleKeys.Employee);

        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/employee/{me.EmployeeId}", me.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/edit/{me.EmployeeId}", me.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/employee/{other.EmployeeId}", me.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/edit/{other.EmployeeId}", me.Token)).StatusCode);
    }

    // ---------- manager ----------

    [Fact]
    public async Task Manager_sees_own_record_and_direct_reports_only()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var manager = await SystemUser(company, SystemRoleKeys.Manager);
        var report1 = await SystemUser(company, SystemRoleKeys.Employee, manager: manager.EmployeeId);
        var report2 = await SystemUser(company, SystemRoleKeys.Employee, manager: manager.EmployeeId);
        var peer = await SystemUser(company, SystemRoleKeys.Employee);
        var reportOfReport = await SystemUser(company, SystemRoleKeys.Employee, manager: report1.EmployeeId);

        var ids = await Ids(await Get(company, "/api/probe/list", manager.Token));

        Assert.Equal(new[] { manager.EmployeeId!.Value, report1.EmployeeId!.Value, report2.EmployeeId!.Value }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/employee/{report1.EmployeeId}", manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/employee/{peer.EmployeeId}", manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/employee/{reportOfReport.EmployeeId}", manager.Token)).StatusCode);
    }

    [Fact]
    public async Task Manager_cannot_edit_a_direct_report()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var manager = await SystemUser(company, SystemRoleKeys.Manager);
        var report = await SystemUser(company, SystemRoleKeys.Employee, manager: manager.EmployeeId);

        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/edit/{manager.EmployeeId}", manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/edit/{report.EmployeeId}", manager.Token)).StatusCode);
    }

    [Fact]
    public async Task Manager_without_an_employee_record_sees_nothing()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var manager = await SystemUser(company, SystemRoleKeys.Manager, withEmployee: false);
        await SystemUser(company, SystemRoleKeys.Employee);

        Assert.Empty(await Ids(await Get(company, "/api/probe/list", manager.Token)));
    }

    // ---------- HR admin ----------

    [Fact]
    public async Task HrAdmin_reaches_every_employee_of_their_company_and_none_of_another()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var otherCompany = await AuthzTestData.CreateCompanyAsync(_fx);
        var hr = await SystemUser(company, SystemRoleKeys.HrAdmin);
        var e1 = await SystemUser(company, SystemRoleKeys.Employee);
        var e2 = await SystemUser(company, SystemRoleKeys.Manager);
        var foreign = await SystemUser(otherCompany, SystemRoleKeys.Employee);

        var ids = await Ids(await Get(company, "/api/probe/list", hr.Token));

        Assert.Equal(new[] { hr.EmployeeId!.Value, e1.EmployeeId!.Value, e2.EmployeeId!.Value }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/employee/{e1.EmployeeId}", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(company, $"/api/probe/edit/{e1.EmployeeId}", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/employee/{foreign.EmployeeId}", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/edit/{foreign.EmployeeId}", hr.Token)).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_has_no_cross_company_access_yet()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var otherCompany = await AuthzTestData.CreateCompanyAsync(_fx);
        var superAdmin = await SystemUser(company, SystemRoleKeys.SuperAdmin);
        var foreign = await SystemUser(otherCompany, SystemRoleKeys.Employee);

        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/list", superAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, $"/api/probe/employee/{foreign.EmployeeId}", superAdmin.Token)).StatusCode);
    }

    // ---------- deny by default / missing permissions ----------

    [Fact]
    public async Task Role_without_the_permission_gets_403_and_empty_permissions_deny_everything()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, "{}");
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, roleId, "Custom");

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/context", user.Token)).StatusCode);
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[\"employees\"]")]
    [InlineData("{\"employees\": \"view_own\"}")]
    [InlineData("{\"employees\": [\"fly\"]}")]
    [InlineData("{\"nonsense\": [\"view_own\"]}")]
    [InlineData("{\"employees\": [1, null, {\"a\": 1}]}")]
    public async Task Malformed_or_unknown_permissions_grant_nothing(string permissionsJson)
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, permissionsJson);
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, roleId, "Custom");

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/context", user.Token)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_permission_denies_even_for_the_most_privileged_role()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var superAdmin = await SystemUser(company, SystemRoleKeys.SuperAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/unknown-permission", superAdmin.Token)).StatusCode);
    }

    [Fact]
    public async Task An_action_that_declares_nothing_is_denied_for_an_authenticated_caller()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var superAdmin = await SystemUser(company, SystemRoleKeys.SuperAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/undeclared", superAdmin.Token)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_unauthenticated_requests_behave_as_declared()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);

        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/anonymous", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(company, "/api/probe/list", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(company, "/api/probe/undeclared", null)).StatusCode);
    }

    // ---------- permissions come from the database on every request (D5) ----------

    [Fact]
    public async Task Changing_the_role_permissions_changes_the_next_request_with_the_same_token()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, "{\"employees\":[\"view_own\",\"view_team\"]}");
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, roleId, "Custom");

        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/list", user.Token)).StatusCode);

        await SetPermissionsAsync(roleId, "{\"employees\":[\"view_own\"]}");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);   // revoked, same token
        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/context", user.Token)).StatusCode);

        await SetPermissionsAsync(roleId, "{\"employees\":[\"view_own\",\"view_team\"]}");
        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/list", user.Token)).StatusCode);          // granted again, same token

        await SetPermissionsAsync(roleId, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/context", user.Token)).StatusCode);
    }

    [Fact]
    public async Task Reassigning_the_user_to_another_role_applies_to_the_next_request_with_the_same_token()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await SystemUser(company, SystemRoleKeys.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);

        await using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == user.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.RoleId, AuthzTestData.SystemRoleId(SystemRoleKeys.Manager)));
        }

        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/list", user.Token)).StatusCode);          // JWT still says "employee"
    }

    private async Task SetPermissionsAsync(Guid roleId, string json)
    {
        await using var db = _fx.CreateDbContext();
        await db.Roles.Where(r => r.Id == roleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Permissions, json));
    }

    // ---------- fail closed on identity / consistency ----------

    [Fact]
    public async Task A_deactivated_user_is_forbidden_on_the_next_request()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await SystemUser(company, SystemRoleKeys.HrAdmin);
        Assert.Equal(HttpStatusCode.OK, (await Get(company, "/api/probe/list", user.Token)).StatusCode);

        await using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);
    }

    [Fact]
    public async Task A_role_belonging_to_another_company_fails_closed()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var other = await AuthzTestData.CreateCompanyAsync(_fx);
        var foreignRole = await AuthzTestData.CreateCompanyRoleAsync(_fx, other.CompanyId, "{\"employees\":[\"view_own\",\"view_team\",\"view_all\"]}");
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, foreignRole, "Custom");

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);
    }

    [Fact]
    public async Task A_company_less_role_that_is_not_a_system_role_fails_closed()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        Guid roleId;

        await using (var db = _fx.CreateDbContext())
        {
            roleId = Guid.NewGuid();
            db.Roles.Add(new HRMS.Domain.Entities.Role
            {
                Id = roleId, CompanyId = null, Name = "orphan-" + roleId.ToString("N")[..8], DisplayName = "Orphan",
                Permissions = "{\"employees\":[\"view_own\",\"view_team\"]}", IsSystem = false
            });
            await db.SaveChangesAsync();
        }

        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, roleId, "orphan");

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);
    }

    [Fact]
    public async Task An_employee_record_belonging_to_another_company_fails_closed()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var other = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, AuthzTestData.SystemRoleId(SystemRoleKeys.HrAdmin),
            SystemRoleKeys.HrAdmin, employeeCompanyId: other.CompanyId, departmentId: other.DepartmentId);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/probe/list", user.Token)).StatusCode);
    }

    [Fact]
    public async Task A_token_for_one_company_is_rejected_on_another_companys_host()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var other = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await SystemUser(company, SystemRoleKeys.HrAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(other, "/api/probe/list", user.Token)).StatusCode);
    }

    [Fact]
    public async Task The_current_user_exposes_user_company_role_key_and_employee_id()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var manager = await SystemUser(company, SystemRoleKeys.Manager);
        var noEmployee = await SystemUser(company, SystemRoleKeys.HrAdmin, withEmployee: false);

        var withEmployee = await (await Get(company, "/api/probe/context", manager.Token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(manager.UserId, withEmployee.GetProperty("userId").GetGuid());
        Assert.Equal(company.CompanyId, withEmployee.GetProperty("companyId").GetGuid());
        Assert.Equal("manager", withEmployee.GetProperty("roleKey").GetString());
        Assert.Equal(manager.EmployeeId, withEmployee.GetProperty("employeeId").GetGuid());

        var without = await (await Get(company, "/api/probe/context", noEmployee.Token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hr_admin", without.GetProperty("roleKey").GetString());
        Assert.Equal(JsonValueKind.Null, without.GetProperty("employeeId").ValueKind);
    }

    // ---------- /api/auth/me and login contract ----------

    [Fact]
    public async Task Login_and_me_return_a_stable_role_key_and_a_permission_object_not_a_string()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await SystemUser(company, SystemRoleKeys.Manager);
        var password = TestData.NewPassword();

        await using (var db = _fx.CreateDbContext())
        {
            var hash = TestData.HashPassword(password);
            await db.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash));
        }

        var client = _factory.ClientFor(company.Domain);
        var loginResponse = await client.PostLoginAsync(user.Email, password);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = login.GetProperty("token").GetString()!;
        var loginUser = login.GetProperty("user");

        Assert.Equal("manager", loginUser.GetProperty("role").GetString());
        Assert.Equal("Manager", loginUser.GetProperty("roleDisplayName").GetString());
        Assert.Equal(JsonValueKind.Object, loginUser.GetProperty("permissions").ValueKind);
        Assert.Equal("view_team", loginUser.GetProperty("permissions").GetProperty("employees").EnumerateArray().Select(e => e.GetString()).Single(a => a == "view_team"));

        var meResponse = await client.GetMeAsync(token);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("manager", me.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Object, me.GetProperty("permissions").ValueKind);
        Assert.Equal(user.EmployeeId, me.GetProperty("employeeId").GetGuid());
        Assert.Contains("view_team", me.GetProperty("permissions").GetProperty("employees").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("view_all", me.GetProperty("permissions").GetProperty("employees").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_reflects_a_role_permission_change_without_a_new_token()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, "{\"employees\":[\"view_own\"]}", "Custom");
        var user = await AuthzTestData.AddUserAsync(_fx, _factory, company, roleId, "Custom");

        var before = await (await Get(company, "/api/auth/me", user.Token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, before.GetProperty("permissions").GetProperty("employees").GetArrayLength());

        await SetPermissionsAsync(roleId, "{\"employees\":[\"view_own\",\"edit_own\"],\"unknown\":[\"x\"]}");

        var after = await (await Get(company, "/api/auth/me", user.Token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, after.GetProperty("permissions").GetProperty("employees").GetArrayLength());
        Assert.False(after.GetProperty("permissions").TryGetProperty("unknown", out _));                  // only catalog permissions are exposed
    }

    [Fact]
    public async Task Me_for_a_deactivated_user_is_forbidden_and_logout_still_works()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var user = await SystemUser(company, SystemRoleKeys.Employee);

        await using (var db = _fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(company, "/api/auth/me", user.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor(company.Domain).LogoutAsync(user.Token)).StatusCode);
    }

    // ---------- cross-company references ----------

    [Theory]
    [InlineData("employee")]
    [InlineData("department")]
    [InlineData("user")]
    public async Task References_to_rows_in_the_callers_company_are_accepted_and_foreign_ones_rejected(string kind)
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var other = await AuthzTestData.CreateCompanyAsync(_fx);
        var hr = await SystemUser(company, SystemRoleKeys.HrAdmin);
        var own = await SystemUser(company, SystemRoleKeys.Employee);
        var foreign = await SystemUser(other, SystemRoleKeys.Employee);

        (Guid ownId, Guid foreignId) = kind switch
        {
            "employee" => (own.EmployeeId!.Value, foreign.EmployeeId!.Value),
            "department" => (company.DepartmentId, other.DepartmentId),
            _ => (own.UserId, foreign.UserId)
        };

        Assert.Equal(HttpStatusCode.OK, (await Post(company, $"/api/probe/reference?kind={kind}&id={ownId}", hr.Token)).StatusCode);

        var rejected = await Post(company, $"/api/probe/reference?kind={kind}&id={foreignId}", hr.Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(company, $"/api/probe/reference?kind={kind}&id={Guid.NewGuid()}", hr.Token)).StatusCode);

        // The answer must not reveal whether the foreign row exists: same body as for a random id.
        var unknownBody = await (await Post(company, $"/api/probe/reference?kind={kind}&id={Guid.NewGuid()}", hr.Token)).Content.ReadAsStringAsync();
        Assert.Equal(unknownBody, await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_role_reference_may_be_a_system_role_or_the_callers_own_company_role_but_not_another_companys()
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var other = await AuthzTestData.CreateCompanyAsync(_fx);
        var hr = await SystemUser(company, SystemRoleKeys.HrAdmin);
        var own = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, "{}");
        var foreign = await AuthzTestData.CreateCompanyRoleAsync(_fx, other.CompanyId, "{}");

        Assert.Equal(HttpStatusCode.OK, (await Post(company, $"/api/probe/reference?kind=role&id={AuthzTestData.SystemRoleId(SystemRoleKeys.Manager)}", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(company, $"/api/probe/reference?kind=role&id={own}", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(company, $"/api/probe/reference?kind=role&id={foreign}", hr.Token)).StatusCode);
    }

    private Task<HttpResponseMessage> Post(AuthzCompany company, string url, string token)
        => _factory.ClientFor(company.Domain).SendAsync(HttpMethod.Post, url, token);
}
