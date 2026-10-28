using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Employees;

/// <summary>PUT /api/employees/{id} against the real API and PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeUpdateTests : EmployeeApiTestBase
{
    public EmployeeUpdateTests(PostgresFixture fx) : base(fx) { }

    private sealed record Setup(AuthzCompany Company, AuthzUser Hr, SeededEmployee Manager, SeededEmployee Target, Guid FinanceId);

    private async Task<Setup> SetupAsync()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var manager = await SeedAsync(company, "Mira Manager", "EMP-0001", SystemRoleKeys.Manager);
        var target = await SeedAsync(company, "Tara Target", "EMP-0010", designation: "Engineer", managerEmployeeId: manager.EmployeeId, joiningDate: new DateOnly(2024, 1, 15));
        var finance = await AddDepartmentAsync(company.CompanyId, "Finance");
        return new Setup(company, hr, manager, target, finance);
    }

    private static Dictionary<string, object?> UpdateBody(
        Guid departmentId, Guid? managerId, string name = "Tara Target", string designation = "Engineer",
        string joiningDate = "2024-01-15", string status = "active", string? lastWorkingDate = null)
        => new()
        {
            ["name"] = name, ["departmentId"] = departmentId, ["managerId"] = managerId, ["designation"] = designation,
            ["joiningDate"] = joiningDate, ["status"] = status, ["lastWorkingDate"] = lastWorkingDate
        };

    private Task<HttpResponseMessage> PutAsync(Setup s, Guid employeeId, string? token, object body)
        => CallAsync(s.Company, HttpMethod.Put, $"/api/employees/{employeeId}", token, body);

    // ---------- success and audit ----------

    [Fact]
    public async Task A_valid_update_changes_the_employee_and_the_account_name_and_returns_the_dto()
    {
        var s = await SetupAsync();

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.FinanceId, null, "Tara T. Target", "Senior Engineer", "2024-02-01", "probation"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("Tara T. Target", body.GetProperty("name").GetString());
        Assert.Equal("Senior Engineer", body.GetProperty("designation").GetString());
        Assert.Equal(s.FinanceId, body.GetProperty("departmentId").GetGuid());
        Assert.Equal("Finance", body.GetProperty("departmentName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("managerId").ValueKind);          // PUT replaces: null clears the manager
        Assert.Equal("2024-02-01", body.GetProperty("joiningDate").GetString());
        Assert.Equal("probation", body.GetProperty("status").GetString());
        Assert.Equal("EMP-0010", body.GetProperty("empCode").GetString());                 // unchanged

        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal("Tara T. Target", user.Name);
        Assert.Equal("Senior Engineer", employee.Designation);
        Assert.Equal(s.FinanceId, employee.DepartmentId);
        Assert.Null(employee.ManagerId);
        Assert.Equal(EmployeeStatus.Probation, employee.Status);
        Assert.Equal(new DateOnly(2024, 2, 1), employee.JoiningDate);
    }

    [Fact]
    public async Task The_audit_row_records_the_exact_before_and_after_state()
    {
        var s = await SetupAsync();

        await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.FinanceId, null, "Tara T. Target", "Senior Engineer", "2024-02-01", "contract"));

        var row = Assert.Single(await AuditAsync(s.Company.CompanyId, "employee.updated"));
        Assert.Equal("employee", row.Module);
        Assert.Equal(s.Target.EmployeeId, row.TargetId);
        Assert.Equal(s.Hr.UserId, row.UserId);
        Assert.Equal(s.Company.CompanyId, row.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, row.IpAddress);

        using var oldDoc = JsonDocument.Parse(row.OldValue!);
        using var newDoc = JsonDocument.Parse(row.NewValue!);
        var before = oldDoc.RootElement;
        var after = newDoc.RootElement;

        Assert.Equal("Tara Target", before.GetProperty("name").GetString());
        Assert.Equal("Engineer", before.GetProperty("designation").GetString());
        Assert.Equal(s.Company.DepartmentId, before.GetProperty("departmentId").GetGuid());
        Assert.Equal(s.Manager.EmployeeId, before.GetProperty("managerId").GetGuid());
        Assert.Equal("2024-01-15", before.GetProperty("joiningDate").GetString());
        Assert.Equal("active", before.GetProperty("status").GetString());

        Assert.Equal("Tara T. Target", after.GetProperty("name").GetString());
        Assert.Equal("Senior Engineer", after.GetProperty("designation").GetString());
        Assert.Equal(s.FinanceId, after.GetProperty("departmentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("managerId").ValueKind);
        Assert.Equal("2024-02-01", after.GetProperty("joiningDate").GetString());
        Assert.Equal("contract", after.GetProperty("status").GetString());

        // Full state on both sides: unchanged values are present too, and identical.
        Assert.Equal(before.GetProperty("empCode").GetString(), after.GetProperty("empCode").GetString());
        Assert.Equal(s.Target.Email, after.GetProperty("email").GetString());
        Assert.Equal(s.Target.EmployeeId, after.GetProperty("id").GetGuid());
        Assert.False(after.TryGetProperty("passwordHash", out _));
        Assert.DoesNotContain("not-a-real-hash", row.OldValue + row.NewValue);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_is_still_audited_with_equal_before_and_after()
    {
        var s = await SetupAsync();

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await AuditAsync(s.Company.CompanyId, "employee.updated"));
        Assert.Equal(row.OldValue, row.NewValue);
    }

    // ---------- status and last working date ----------

    [Fact]
    public async Task Notice_period_requires_a_last_working_date_and_active_clears_it()
    {
        var s = await SetupAsync();

        var notice = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, status: "notice_period", lastWorkingDate: "2024-06-30"));
        Assert.Equal(HttpStatusCode.OK, notice.StatusCode);
        Assert.Equal("2024-06-30", (await JsonOf(notice)).GetProperty("lastWorkingDate").GetString());
        Assert.Equal(new DateOnly(2024, 6, 30), (await LoadAsync(s.Target.EmployeeId)).Employee.LastWorkingDate);

        var back = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, status: "active"));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Null((await LoadAsync(s.Target.EmployeeId)).Employee.LastWorkingDate);
    }

    [Theory]
    [InlineData("active", "2024-06-30")]                    // a last working date only belongs to a leaving employee
    [InlineData("probation", "2024-06-30")]
    [InlineData("contract", "2024-06-30")]
    [InlineData("notice_period", null)]                     // and is required for notice_period
    [InlineData("notice_period", "2023-12-31")]             // before the joining date
    [InlineData("notice_period", "2101-01-01")]
    [InlineData("inactive", null)]                          // only through the deactivate endpoint
    [InlineData("inactive", "2024-06-30")]
    [InlineData("retired", null)]                           // not one of the five specified values
    [InlineData("", null)]
    public async Task Invalid_status_combinations_are_rejected_and_change_nothing(string status, string? lastWorkingDate)
    {
        var s = await SetupAsync();
        var before = await LoadAsync(s.Target.EmployeeId);

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, status: status, lastWorkingDate: lastWorkingDate));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal(before.Employee.Status, after.Employee.Status);
        Assert.Equal(before.Employee.LastWorkingDate, after.Employee.LastWorkingDate);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }

    [Fact]
    public async Task Status_values_are_accepted_case_insensitively()
    {
        var s = await SetupAsync();

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, status: "PROBATION"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("probation", (await JsonOf(response)).GetProperty("status").GetString());
    }

    // ---------- other validation ----------

    [Fact]
    public async Task Invalid_field_values_are_rejected_and_change_nothing()
    {
        var s = await SetupAsync();
        var before = await LoadAsync(s.Target.EmployeeId);
        var d = s.Company.DepartmentId;
        var m = s.Manager.EmployeeId;

        var attempts = new[]
        {
            UpdateBody(d, m, name: ""),
            UpdateBody(d, m, name: new string('N', 101)),
            UpdateBody(d, m, designation: ""),
            UpdateBody(d, m, designation: new string('D', 101)),
            UpdateBody(d, m, joiningDate: "1899-12-31"),
            UpdateBody(d, m, joiningDate: "2101-01-01"),
            UpdateBody(Guid.Empty, m)
        };

        foreach (var body in attempts)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, body)).StatusCode);
        }

        var after = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal(before.User.Name, after.User.Name);
        Assert.Equal(before.Employee.Designation, after.Employee.Designation);
        Assert.Equal(before.Employee.DepartmentId, after.Employee.DepartmentId);
        Assert.Equal(before.Employee.JoiningDate, after.Employee.JoiningDate);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }

    [Fact]
    public async Task A_joining_date_after_the_stored_last_working_date_is_rejected()
    {
        var s = await SetupAsync();
        await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, status: "notice_period", lastWorkingDate: "2024-06-30"));

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, joiningDate: "2025-01-01", status: "active"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                                // active clears the date, so this is valid
        var stillNotice = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, joiningDate: "2025-01-01", status: "notice_period", lastWorkingDate: "2024-06-30"));
        Assert.Equal(HttpStatusCode.BadRequest, stillNotice.StatusCode);                     // joining after the last working date
    }

    // ---------- references and tenant isolation ----------

    [Fact]
    public async Task Unknown_and_foreign_departments_and_managers_are_rejected_without_revealing_which()
    {
        var s = await SetupAsync();
        var other = await NewCompanyAsync();
        var foreignManager = await SeedAsync(other, "Foreign Boss", "EMP-0001", SystemRoleKeys.Manager);
        var before = await LoadAsync(s.Target.EmployeeId);

        var unknownDepartment = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(Guid.NewGuid(), s.Manager.EmployeeId));
        var foreignDepartment = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(other.DepartmentId, s.Manager.EmployeeId));
        var unknownManager = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, Guid.NewGuid()));
        var foreignManagerResponse = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, foreignManager.EmployeeId));

        Assert.Equal(HttpStatusCode.BadRequest, unknownDepartment.StatusCode);
        Assert.Equal(await unknownDepartment.Content.ReadAsStringAsync(), await foreignDepartment.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, unknownManager.StatusCode);
        Assert.Equal(await unknownManager.Content.ReadAsStringAsync(), await foreignManagerResponse.Content.ReadAsStringAsync());
        Assert.Equal(before.Employee.DepartmentId, (await LoadAsync(s.Target.EmployeeId)).Employee.DepartmentId);
        Assert.Equal(before.Employee.ManagerId, (await LoadAsync(s.Target.EmployeeId)).Employee.ManagerId);
    }

    [Fact]
    public async Task Reporting_rules_self_manager_cycles_and_inactive_managers()
    {
        var s = await SetupAsync();
        var inactive = await SeedAsync(s.Company, "Gone Boss", "EMP-0020", SystemRoleKeys.Manager, status: EmployeeStatus.Inactive, userActive: false);

        var self = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, s.Target.EmployeeId));
        // Target reports to Manager; making Target the manager of Manager would create a cycle.
        var cycle = await PutAsync(s, s.Manager.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, s.Target.EmployeeId, "Mira Manager"));
        var inactiveManager = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, inactive.EmployeeId));

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
        Assert.Contains("circular", await cycle.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, inactiveManager.StatusCode);
        Assert.Null((await LoadAsync(s.Manager.EmployeeId)).Employee.ManagerId);
    }

    [Fact]
    public async Task A_longer_reporting_cycle_is_also_detected()
    {
        var s = await SetupAsync();
        var middle = await SeedAsync(s.Company, "Mid Level", "EMP-0030", managerEmployeeId: s.Target.EmployeeId);   // Manager <- Target <- Mid
        var bottom = await SeedAsync(s.Company, "Bottom", "EMP-0031", managerEmployeeId: middle.EmployeeId);          // ... <- Bottom

        // Manager reporting to Bottom would close the loop Manager -> Target -> Mid -> Bottom -> Manager.
        var response = await PutAsync(s, s.Manager.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, bottom.EmployeeId, "Mira Manager"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restricted_fields_in_the_body_are_ignored()
    {
        var s = await SetupAsync();
        var other = await NewCompanyAsync();
        var before = await LoadAsync(s.Target.EmployeeId);
        var body = UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, "Tara Renamed");
        body["email"] = "hijack@example.test";
        body["empCode"] = "EMP-9999";
        body["companyId"] = other.CompanyId;
        body["userId"] = Guid.NewGuid();
        body["id"] = Guid.NewGuid();
        body["roleId"] = AuthzTestData.SystemRoleId(SystemRoleKeys.SuperAdmin);
        body["role"] = "super_admin";
        body["isActive"] = false;
        body["passwordHash"] = "x";

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal("Tara Renamed", after.User.Name);                                      // the allowed field changed...
        Assert.Equal(before.User.Email, after.User.Email);                                  // ...the restricted ones did not
        Assert.Equal(before.Employee.EmpCode, after.Employee.EmpCode);
        Assert.Equal(s.Company.CompanyId, after.Employee.CompanyId);
        Assert.Equal(s.Company.CompanyId, after.User.CompanyId);
        Assert.Equal(before.User.Id, after.Employee.UserId);
        Assert.Equal(before.User.RoleId, after.User.RoleId);
        Assert.True(after.User.IsActive);
        Assert.Equal(before.User.PasswordHash, after.User.PasswordHash);
        Assert.Equal(s.Target.EmployeeId, after.Employee.Id);
    }

    [Fact]
    public async Task Another_companys_employee_cannot_be_updated_and_looks_like_a_missing_one()
    {
        var s = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var foreign = await SeedAsync(other, "Fiona Foreign", "EMP-0001");
        var beforeForeign = await LoadAsync(foreign.EmployeeId);

        var viaOurHr = await PutAsync(s, foreign.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, null, "Taken Over"));
        var missing = await PutAsync(s, Guid.NewGuid(), s.Hr.Token, UpdateBody(s.Company.DepartmentId, null));
        var theirHrOnOurHost = await PutAsync(s, s.Target.EmployeeId, otherHr.Token, UpdateBody(s.Company.DepartmentId, null));

        Assert.Equal(HttpStatusCode.NotFound, viaOurHr.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await viaOurHr.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, theirHrOnOurHost.StatusCode);                // tenant mismatch on the host
        var afterForeign = await LoadAsync(foreign.EmployeeId);
        Assert.Equal(beforeForeign.User.Name, afterForeign.User.Name);
        Assert.Equal(beforeForeign.Employee.DepartmentId, afterForeign.Employee.DepartmentId);
        Assert.Empty(await AuditAsync(other.CompanyId, "employee.updated"));
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }

    // ---------- permissions ----------

    [Fact]
    public async Task Only_the_edit_any_permission_can_update_and_nobody_can_self_edit_yet()
    {
        var s = await SetupAsync();
        var body = UpdateBody(s.Company.DepartmentId, s.Manager.EmployeeId, "Changed Name");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(s, s.Target.EmployeeId, null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(s, s.Target.EmployeeId, s.Manager.Token, body)).StatusCode);        // manager over a direct report
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(s, s.Target.EmployeeId, s.Target.Token, body)).StatusCode);         // employee on themself (D7: no self-editable fields are specified)
        Assert.Equal("Tara Target", (await LoadAsync(s.Target.EmployeeId)).User.Name);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }

    // ---------- state and rollback ----------

    [Fact]
    public async Task A_deactivated_employee_cannot_be_updated()
    {
        var s = await SetupAsync();
        var gone = await SeedAsync(s.Company, "Gone Gary", "EMP-0040", status: EmployeeStatus.Inactive, userActive: false);

        var response = await PutAsync(s, gone.EmployeeId, s.Hr.Token, UpdateBody(s.Company.DepartmentId, null, "Gone Gary"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_update_back()
    {
        var s = await SetupAsync();
        var before = await LoadAsync(s.Target.EmployeeId);
        Switch.AuditStageFails = true;

        var response = await PutAsync(s, s.Target.EmployeeId, s.Hr.Token,
            UpdateBody(s.FinanceId, null, "Must Not Persist", "Must Not Persist", "2024-02-01", "contract"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("simulated", await response.Content.ReadAsStringAsync());
        var after = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal(before.User.Name, after.User.Name);
        Assert.Equal(before.Employee.Designation, after.Employee.Designation);
        Assert.Equal(before.Employee.DepartmentId, after.Employee.DepartmentId);
        Assert.Equal(before.Employee.ManagerId, after.Employee.ManagerId);
        Assert.Equal(before.Employee.Status, after.Employee.Status);
        Assert.Empty(await AuditAsync(s.Company.CompanyId, "employee.updated"));
    }
}
