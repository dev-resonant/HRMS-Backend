using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Employees;

/// <summary>PUT /api/employees/{id}/deactivate against the real API and PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeDeactivateTests : EmployeeApiTestBase
{
    public EmployeeDeactivateTests(PostgresFixture fx) : base(fx) { }

    private sealed record Setup(AuthzCompany Company, AuthzUser Hr, SeededEmployee Manager, SeededEmployee Target);

    private async Task<Setup> SetupAsync()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var manager = await SeedAsync(company, "Mira Manager", "EMP-0001", SystemRoleKeys.Manager);
        var target = await SeedAsync(company, "Tara Target", "EMP-0010", managerEmployeeId: manager.EmployeeId, joiningDate: new DateOnly(2024, 1, 15));
        return new Setup(company, hr, manager, target);
    }

    private Task<HttpResponseMessage> DeactivateAsync(Setup s, Guid employeeId, string? token, string? lastWorkingDate = "2024-12-31")
        => CallAsync(s.Company, HttpMethod.Put, $"/api/employees/{employeeId}/deactivate", token,
            lastWorkingDate is null ? new { } : new { lastWorkingDate });

    // ---------- success ----------

    [Fact]
    public async Task HR_deactivates_an_employee_status_account_and_sessions_in_one_step()
    {
        var s = await SetupAsync();
        await TestData.AddSessionAsync(Fx, s.Target.UserId, "second-device-token", DateTime.UtcNow.AddHours(1));      // 2 sessions in total
        var hrSessionsBefore = (await TestData.GetSessionsAsync(Fx, s.Hr.UserId)).Count;

        var response = await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token, "2024-12-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(2, body.GetProperty("sessionsRevoked").GetInt32());
        Assert.Equal(0, body.GetProperty("directReportsNeedingReassignment").GetInt32());
        var dto = body.GetProperty("employee");
        Assert.Equal("inactive", dto.GetProperty("status").GetString());
        Assert.False(dto.GetProperty("isActive").GetBoolean());
        Assert.Equal("2024-12-31", dto.GetProperty("lastWorkingDate").GetString());

        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.False(user.IsActive);                                                       // login blocked
        Assert.Equal(EmployeeStatus.Inactive, employee.Status);
        Assert.Equal(new DateOnly(2024, 12, 31), employee.LastWorkingDate);
        Assert.Empty(await TestData.GetSessionsAsync(Fx, s.Target.UserId));              // every device logged out
        Assert.Equal(hrSessionsBefore, (await TestData.GetSessionsAsync(Fx, s.Hr.UserId)).Count);     // nobody else's sessions touched
        Assert.Single(await TestData.GetSessionsAsync(Fx, s.Manager.UserId));
    }

    [Fact]
    public async Task The_audit_row_has_the_exact_target_actor_and_before_after_state()
    {
        var s = await SetupAsync();
        await TestData.AddSessionAsync(Fx, s.Target.UserId, "second-device-token", DateTime.UtcNow.AddHours(1));

        await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token, "2024-12-31");

        var row = Assert.Single(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
        Assert.Equal("employee", row.Module);
        Assert.Equal(s.Target.EmployeeId, row.TargetId);
        Assert.Equal(s.Hr.UserId, row.UserId);
        Assert.Equal(s.Company.CompanyId, row.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, row.IpAddress);

        using var oldDoc = JsonDocument.Parse(row.OldValue!);
        using var newDoc = JsonDocument.Parse(row.NewValue!);
        Assert.Equal("active", oldDoc.RootElement.GetProperty("status").GetString());
        Assert.True(oldDoc.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, oldDoc.RootElement.GetProperty("lastWorkingDate").ValueKind);

        var after = newDoc.RootElement;                                              // same shape as "before", plus what the operation did
        Assert.Equal("inactive", after.GetProperty("status").GetString());
        Assert.False(after.GetProperty("isActive").GetBoolean());
        Assert.Equal("2024-12-31", after.GetProperty("lastWorkingDate").GetString());
        Assert.Equal(s.Target.EmployeeId, after.GetProperty("id").GetGuid());
        Assert.Equal(2, after.GetProperty("sessionsRevoked").GetInt32());
        Assert.Equal(0, after.GetProperty("directReportsNeedingReassignment").GetInt32());
        // Before and after can be compared field by field: every field of "before" exists in "after".
        Assert.Empty(oldDoc.RootElement.EnumerateObject().Select(p => p.Name).Except(after.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain("not-a-real-hash", row.OldValue + row.NewValue);
        Assert.DoesNotContain("second-device-token", row.OldValue + row.NewValue);
    }

    [Fact]
    public async Task The_deactivated_employees_old_token_stops_working_and_login_is_blocked()
    {
        var s = await SetupAsync();
        const string password = "Known-Test-Pw-1234!";
        await using (var db = Fx.CreateDbContext())
        {
            var hash = TestData.HashPassword(password);
            await db.Users.Where(u => u.Id == s.Target.UserId).ExecuteUpdateAsync(x => x.SetProperty(u => u.PasswordHash, hash));
        }

        var client = Factory.ClientFor(s.Company.Domain);
        Assert.Equal(HttpStatusCode.OK, (await client.PostLoginAsync(s.Target.Email, password)).StatusCode);        // can log in before
        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(s.Target.Token)).StatusCode);                        // token works before

        Assert.Equal(HttpStatusCode.OK, (await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetMeAsync(s.Target.Token)).StatusCode);              // session was deleted
        var login = await client.PostLoginAsync(s.Target.Email, password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);                                                     // account is deactivated
        Assert.Contains("deactivated", await login.Content.ReadAsStringAsync());
        Assert.Empty(await TestData.GetSessionsAsync(Fx, s.Target.UserId));                                          // the refused login created no new session
    }

    [Fact]
    public async Task Historical_data_is_preserved_and_direct_reports_are_reported_not_changed()
    {
        var s = await SetupAsync();
        var report1 = await SeedAsync(s.Company, "Report One", "EMP-0011", managerEmployeeId: s.Target.EmployeeId);
        var report2 = await SeedAsync(s.Company, "Report Two", "EMP-0012", managerEmployeeId: s.Target.EmployeeId);
        await SeedAsync(s.Company, "Report Gone", "EMP-0013", managerEmployeeId: s.Target.EmployeeId, status: EmployeeStatus.Inactive, userActive: false);
        var created = await CallAsync(s.Company, HttpMethod.Post, "/api/employees", s.Hr.Token, CreateBody(s.Company.DepartmentId, email: "history@example.test"));
        var createdId = (await JsonOf(created)).GetProperty("id").GetGuid();
        var before = await LoadAsync(s.Target.EmployeeId);

        var response = await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token);

        Assert.Equal(2, (await JsonOf(response)).GetProperty("directReportsNeedingReassignment").GetInt32());     // the inactive report is not counted
        var after = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal(before.Employee.EmpCode, after.Employee.EmpCode);                    // the record is kept, never deleted
        Assert.Equal(before.Employee.DepartmentId, after.Employee.DepartmentId);
        Assert.Equal(before.Employee.ManagerId, after.Employee.ManagerId);
        Assert.Equal(before.Employee.JoiningDate, after.Employee.JoiningDate);
        Assert.Equal(before.Employee.Designation, after.Employee.Designation);
        Assert.Equal(before.User.Email, after.User.Email);
        Assert.Equal(before.User.Name, after.User.Name);
        Assert.Equal(s.Target.EmployeeId, (await LoadAsync(report1.EmployeeId)).Employee.ManagerId);          // reports still point at them
        Assert.Equal(s.Target.EmployeeId, (await LoadAsync(report2.EmployeeId)).Employee.ManagerId);

        // Earlier audit history is untouched and the deactivation is added to it.
        var deactivatedCreated = await CallAsync(s.Company, HttpMethod.Put, $"/api/employees/{createdId}/deactivate", s.Hr.Token, new { lastWorkingDate = "2025-01-31" });
        Assert.Equal(HttpStatusCode.OK, deactivatedCreated.StatusCode);
        var actions = (await AuditAsync(s.Company.CompanyId)).Where(a => a.TargetId == createdId).Select(a => a.Action).ToList();
        Assert.Equal(new[] { "employee.created", "employee.deactivated" }, actions);
    }

    // ---------- invalid requests and transitions ----------

    [Fact]
    public async Task Deactivating_twice_is_a_409()
    {
        var s = await SetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token)).StatusCode);

        var second = await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
    }

    [Theory]
    [InlineData(null)]                    // missing
    [InlineData("0001-01-01")]
    [InlineData("1899-12-31")]
    [InlineData("2101-01-01")]
    [InlineData("2023-12-31")]            // before the joining date (2024-01-15)
    public async Task An_invalid_last_working_date_is_rejected_and_nothing_changes(string? lastWorkingDate)
    {
        var s = await SetupAsync();

        var response = await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token, lastWorkingDate);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.True(user.IsActive);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Single(await TestData.GetSessionsAsync(Fx, s.Target.UserId));              // the session survived
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
    }

    [Fact]
    public async Task Nobody_can_deactivate_themselves()
    {
        var s = await SetupAsync();

        var response = await DeactivateAsync(s, s.Hr.EmployeeId!.Value, s.Hr.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((await LoadAsync(s.Hr.EmployeeId!.Value)).User.IsActive);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
    }

    // ---------- permissions and tenants ----------

    [Fact]
    public async Task Only_the_deactivate_permission_may_deactivate()
    {
        var s = await SetupAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeactivateAsync(s, s.Target.EmployeeId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeactivateAsync(s, s.Target.EmployeeId, s.Manager.Token)).StatusCode);       // a manager over a direct report
        Assert.Equal(HttpStatusCode.Forbidden, (await DeactivateAsync(s, s.Target.EmployeeId, s.Target.Token)).StatusCode);        // an employee on themself
        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.True(user.IsActive);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
    }

    [Fact]
    public async Task Another_companys_employee_cannot_be_deactivated_and_looks_like_a_missing_one()
    {
        var s = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var foreign = await SeedAsync(other, "Fiona Foreign", "EMP-0001");

        var viaOurHr = await DeactivateAsync(s, foreign.EmployeeId, s.Hr.Token);
        var missing = await DeactivateAsync(s, Guid.NewGuid(), s.Hr.Token);
        var theirHrOnOurHost = await DeactivateAsync(s, s.Target.EmployeeId, otherHr.Token);

        Assert.Equal(HttpStatusCode.NotFound, viaOurHr.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await viaOurHr.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, theirHrOnOurHost.StatusCode);
        Assert.True((await LoadAsync(foreign.EmployeeId)).User.IsActive);
        Assert.Single(await TestData.GetSessionsAsync(Fx, foreign.UserId));
        Assert.True((await LoadAsync(s.Target.EmployeeId)).User.IsActive);
        Assert.Empty(await AuditAsync(other.CompanyId, "employee.deactivated"));
    }

    // ---------- rollback ----------

    [Fact]
    public async Task A_failing_audit_entry_rolls_back_the_status_the_account_and_the_session_deletion()
    {
        var s = await SetupAsync();
        await TestData.AddSessionAsync(Fx, s.Target.UserId, "second-device-token", DateTime.UtcNow.AddHours(1));
        Switch.AuditStageFails = true;

        var response = await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("simulated", await response.Content.ReadAsStringAsync());
        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.True(user.IsActive);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Null(employee.LastWorkingDate);
        Assert.Equal(2, (await TestData.GetSessionsAsync(Fx, s.Target.UserId)).Count);        // the bulk DELETE was rolled back too
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));

        Switch.AuditStageFails = false;
        Assert.Equal(HttpStatusCode.OK, (await DeactivateAsync(s, s.Target.EmployeeId, s.Hr.Token)).StatusCode);       // and it works once the failure is gone
    }
}
