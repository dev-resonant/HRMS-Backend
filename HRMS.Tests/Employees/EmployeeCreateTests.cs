using System.Net;
using System.Text.Json;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>POST /api/employees against the real API and PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeCreateTests : EmployeeApiTestBase
{
    public EmployeeCreateTests(PostgresFixture fx) : base(fx, fixedPassword: true) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    // ---------- success ----------

    [Fact]
    public async Task A_valid_request_creates_the_account_and_the_employee_and_returns_the_dto()
    {
        var (company, hr) = await SetupAsync();
        var manager = await SeedAsync(company, "Mira Manager", "EMP-0900", SystemRoleKeys.Manager);

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token,
            CreateBody(company.DepartmentId, "Asha Rao", "Asha.Rao@Example.TEST", manager.EmployeeId, "Software Engineer", "2024-03-01"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/employees/{id}", response.Headers.Location!.ToString());
        Assert.Equal("EMP-0901", body.GetProperty("empCode").GetString());                 // continues after the existing EMP-0900
        Assert.Equal("Asha Rao", body.GetProperty("name").GetString());
        Assert.Equal("asha.rao@example.test", body.GetProperty("email").GetString());      // normalized
        Assert.Equal(company.DepartmentId, body.GetProperty("departmentId").GetGuid());
        Assert.Equal("Engineering", body.GetProperty("departmentName").GetString());
        Assert.Equal(manager.EmployeeId, body.GetProperty("managerId").GetGuid());
        Assert.Equal("Mira Manager", body.GetProperty("managerName").GetString());
        Assert.Equal("Software Engineer", body.GetProperty("designation").GetString());
        Assert.Equal("2024-03-01", body.GetProperty("joiningDate").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastWorkingDate").ValueKind);
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal("employee", body.GetProperty("role").GetString());

        // The public contract exposes no company, credential or internal account data.
        var propertyNames = body.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(propertyNames, n => n.Contains("company") || n.Contains("password") || n.Contains("hash") || n == "userid");

        var (user, employee) = await LoadAsync(id);
        Assert.Equal(company.CompanyId, employee.CompanyId);
        Assert.Equal(company.CompanyId, user.CompanyId);
        Assert.Equal(AuthzTestData.SystemRoleId(SystemRoleKeys.Employee), user.RoleId);     // default role
        Assert.True(user.IsActive);
        Assert.True(user.IsFirstLogin);                                                     // must reset on first login
        Assert.StartsWith("$2", user.PasswordHash);                                         // a BCrypt hash, never plaintext
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Equal(new DateOnly(2024, 3, 1), employee.JoiningDate);
        Assert.Null(employee.LastWorkingDate);
        Assert.Equal(manager.EmployeeId, employee.ManagerId);
    }

    [Fact]
    public async Task The_audit_row_has_the_exact_target_actor_company_ip_and_payload()
    {
        var (company, hr) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token,
            CreateBody(company.DepartmentId, "Asha Rao", "asha@example.test", null, "Software Engineer", "2024-03-01"));
        var id = (await JsonOf(response)).GetProperty("id").GetGuid();

        var row = Assert.Single(await AuditAsync(company.CompanyId, "employee.created"));
        Assert.Equal("employee", row.Module);
        Assert.Equal(id, row.TargetId);
        Assert.Equal(hr.UserId, row.UserId);                                                // the authenticated actor
        Assert.Equal(company.CompanyId, row.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, row.IpAddress);
        Assert.Null(row.OldValue);

        using var after = JsonDocument.Parse(row.NewValue!);
        var state = after.RootElement;
        Assert.Equal(id, state.GetProperty("id").GetGuid());
        Assert.Equal("EMP-0001", state.GetProperty("empCode").GetString());
        Assert.Equal("Asha Rao", state.GetProperty("name").GetString());
        Assert.Equal("asha@example.test", state.GetProperty("email").GetString());
        Assert.Equal(company.DepartmentId, state.GetProperty("departmentId").GetGuid());
        Assert.Equal("Software Engineer", state.GetProperty("designation").GetString());
        Assert.Equal("2024-03-01", state.GetProperty("joiningDate").GetString());
        Assert.Equal("active", state.GetProperty("status").GetString());
        Assert.True(state.GetProperty("isActive").GetBoolean());
        Assert.False(state.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task The_temporary_password_never_appears_in_the_response_audit_or_logs_and_only_its_hash_is_stored()
    {
        var (company, hr) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));
        var raw = await response.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(raw).RootElement.GetProperty("id").GetGuid();
        var (user, _) = await LoadAsync(id);
        var audit = Assert.Single(await AuditAsync(company.CompanyId, "employee.created"));

        Assert.DoesNotContain(FixedPasswordGenerator.Value, raw);
        Assert.DoesNotContain(FixedPasswordGenerator.Value, audit.NewValue + audit.OldValue);
        Assert.DoesNotContain(user.PasswordHash, audit.NewValue + audit.OldValue + raw);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(FixedPasswordGenerator.Value) || e.Message.Contains(user.PasswordHash));
        Assert.NotEqual(FixedPasswordGenerator.Value, user.PasswordHash);
        Assert.True(new BCryptPasswordHasher().Verify(FixedPasswordGenerator.Value, user.PasswordHash));   // the stored hash is of the generated password
    }

    [Fact]
    public async Task Codes_follow_the_spec_format_and_each_company_has_its_own_sequence()
    {
        var (companyA, hrA) = await SetupAsync();
        var (companyB, hrB) = await SetupAsync();

        var first = await JsonOf(await CallAsync(companyA, HttpMethod.Post, "/api/employees", hrA.Token, CreateBody(companyA.DepartmentId)));
        var second = await JsonOf(await CallAsync(companyA, HttpMethod.Post, "/api/employees", hrA.Token, CreateBody(companyA.DepartmentId)));
        var other = await JsonOf(await CallAsync(companyB, HttpMethod.Post, "/api/employees", hrB.Token, CreateBody(companyB.DepartmentId)));

        Assert.Equal("EMP-0001", first.GetProperty("empCode").GetString());
        Assert.Equal("EMP-0002", second.GetProperty("empCode").GetString());
        Assert.Equal("EMP-0001", other.GetProperty("empCode").GetString());
    }

    [Fact]
    public async Task Fields_the_server_decides_cannot_be_set_by_the_client()
    {
        var (company, hr) = await SetupAsync();
        var otherCompany = await NewCompanyAsync();
        var suppliedId = Guid.NewGuid();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, new
        {
            name = "Over Poster",
            email = "over@example.test",
            departmentId = company.DepartmentId,
            designation = "Engineer",
            joiningDate = "2024-03-01",
            // none of these exist in the contract and must be ignored
            companyId = otherCompany.CompanyId,
            userId = Guid.NewGuid(),
            id = suppliedId,
            empCode = "EMP-9999",
            status = "inactive",
            isActive = false,
            isFirstLogin = false,
            roleId = AuthzTestData.SystemRoleId(SystemRoleKeys.SuperAdmin),
            role = "super_admin",
            passwordHash = "x",
            password = "ChosenByClient1!",
            createdBy = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonOf(response)).GetProperty("id").GetGuid();
        var (user, employee) = await LoadAsync(id);
        Assert.Equal(company.CompanyId, employee.CompanyId);
        Assert.Equal(company.CompanyId, user.CompanyId);
        Assert.Equal("EMP-0001", employee.EmpCode);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.True(user.IsActive);
        Assert.True(user.IsFirstLogin);
        Assert.Equal(AuthzTestData.SystemRoleId(SystemRoleKeys.Employee), user.RoleId);
        Assert.NotEqual(suppliedId, id);
        Assert.Equal(0, (await CountsAsync(otherCompany.CompanyId)).Employees);
        Assert.Equal(hr.UserId, Assert.Single(await AuditAsync(company.CompanyId, "employee.created")).UserId);
    }

    // ---------- validation ----------

    private async Task<HttpResponseMessage> PostRawAsync(AuthzCompany company, string token, string json)
    {
        var client = Factory.ClientFor(company.Domain);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/employees")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("designation", "")]
    [InlineData("designation", "   ")]
    [InlineData("email", "")]
    [InlineData("email", "not-an-email")]
    [InlineData("email", "missing-at.example.test")]
    [InlineData("joiningDate", "0001-01-01")]
    [InlineData("joiningDate", "1899-12-31")]
    [InlineData("joiningDate", "2101-01-01")]
    public async Task Invalid_values_return_400_with_field_errors_and_change_nothing(string field, string value)
    {
        var (company, hr) = await SetupAsync();
        var before = await CountsAsync(company.CompanyId);
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Asha Rao", ["email"] = "asha@example.test", ["departmentId"] = company.DepartmentId,
            ["designation"] = "Engineer", ["joiningDate"] = "2024-03-01"
        };
        body[field] = value;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains((await JsonOf(response)).GetProperty("errors").EnumerateObject(), p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    [Fact]
    public async Task Missing_required_fields_are_rejected()
    {
        var (company, hr) = await SetupAsync();

        var response = await PostRawAsync(company, hr.Token, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonOf(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.Contains("name", errors);
        Assert.Contains("email", errors);
        Assert.Contains("departmentid", errors);
        Assert.Contains("designation", errors);
        Assert.Contains("joiningdate", errors);
        Assert.Equal(0, (await CountsAsync(company.CompanyId)).Audit);
    }

    [Theory]
    [InlineData("{\"name\":\"A\",\"email\":\"a@example.test\",\"departmentId\":\"00000000-0000-0000-0000-000000000000\",\"designation\":\"D\",\"joiningDate\":\"2024-02-30\"}")]
    [InlineData("{\"name\":\"A\",\"email\":\"a@example.test\",\"departmentId\":\"not-a-guid\",\"designation\":\"D\",\"joiningDate\":\"2024-03-01\"}")]
    [InlineData("{\"name\":\"A\",\"email\":\"a@example.test\",\"departmentId\":\"00000000-0000-0000-0000-000000000000\",\"designation\":\"D\",\"joiningDate\":\"tomorrow\"}")]
    [InlineData("{not json")]
    public async Task Malformed_json_values_return_400(string json)
    {
        var (company, hr) = await SetupAsync();

        var response = await PostRawAsync(company, hr.Token, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await CountsAsync(company.CompanyId)).Audit);
    }

    [Fact]
    public async Task Maximum_lengths_are_accepted_and_one_more_is_rejected()
    {
        var (company, hr) = await SetupAsync();
        var before = await CountsAsync(company.CompanyId);

        var ok = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token,
            CreateBody(company.DepartmentId, new string('N', 100), designation: new string('D', 100)));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var longName = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, new string('N', 101)));
        var longDesignation = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, designation: new string('D', 101)));
        var longEmail = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: new string('e', 251) + "@x.co"));

        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longDesignation.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longEmail.StatusCode);
        Assert.Equal(before.Employees + 1, (await CountsAsync(company.CompanyId)).Employees);       // only the maximum-length one exists
    }

    // ---------- references ----------

    [Fact]
    public async Task An_unknown_or_foreign_department_is_rejected_with_the_same_answer_and_nothing_is_created()
    {
        var (company, hr) = await SetupAsync();
        var foreign = await NewCompanyAsync();
        var before = await CountsAsync(company.CompanyId);

        var unknown = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(Guid.NewGuid()));
        var foreignDepartment = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(foreign.DepartmentId));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignDepartment.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreignDepartment.Content.ReadAsStringAsync());   // no existence oracle
        Assert.Contains("DepartmentId", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(before, await CountsAsync(company.CompanyId));
        Assert.Equal(0, (await CountsAsync(foreign.CompanyId)).Employees);
    }

    [Fact]
    public async Task An_unknown_foreign_or_inactive_manager_is_rejected()
    {
        var (company, hr) = await SetupAsync();
        var foreign = await NewCompanyAsync();
        var foreignManager = await SeedAsync(foreign, "Foreign Boss", "EMP-0001", SystemRoleKeys.Manager);
        var inactiveManager = await SeedAsync(company, "Gone Boss", "EMP-0002", SystemRoleKeys.Manager, status: EmployeeStatus.Inactive, userActive: false);
        var before = await CountsAsync(company.CompanyId);

        var unknown = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, managerId: Guid.NewGuid()));
        var foreignResponse = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, managerId: foreignManager.EmployeeId));
        var inactive = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, managerId: inactiveManager.EmployeeId));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreignResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
        Assert.Contains("active employee", await inactive.Content.ReadAsStringAsync());
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    // ---------- duplicates ----------

    [Fact]
    public async Task A_duplicate_email_in_the_same_company_is_a_409_and_changes_nothing()
    {
        var (company, hr) = await SetupAsync();
        await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "dup@example.test"));
        var before = await CountsAsync(company.CompanyId);

        var same = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "dup@example.test"));
        var differentCase = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "DUP@Example.Test"));

        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, differentCase.StatusCode);
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    [Fact]
    public async Task The_same_email_in_another_company_is_allowed()
    {
        var (companyA, hrA) = await SetupAsync();
        var (companyB, hrB) = await SetupAsync();
        // Unique per run on purpose: UserEmailConstraintTests counts users by the literal address "shared@example.test" across the whole shared database.
        var sharedEmail = $"cross-company-{Guid.NewGuid():N}@example.test";
        var a = await CallAsync(companyA, HttpMethod.Post, "/api/employees", hrA.Token, CreateBody(companyA.DepartmentId, email: sharedEmail));
        var b = await CallAsync(companyB, HttpMethod.Post, "/api/employees", hrB.Token, CreateBody(companyB.DepartmentId, email: sharedEmail));

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
    }

    [Fact]
    public async Task A_code_that_is_already_taken_is_a_409_without_database_details_and_rolls_everything_back()
    {
        var (company, hr) = await SetupAsync();
        await SeedAsync(company, "Existing", "EMP-0001");
        Switch.FixedEmployeeCode = "EMP-0001";                          // simulates something bypassing the generator
        var before = await CountsAsync(company.CompanyId);

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("employee code", raw);
        Assert.DoesNotContain("IX_", raw);
        Assert.DoesNotContain("duplicate key", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("23505", raw);
        Assert.DoesNotContain("Npgsql", raw);
        Assert.Equal(before, await CountsAsync(company.CompanyId));      // no user, no employee, no audit row
    }

    // ---------- authorization ----------

    [Fact]
    public async Task Only_a_caller_with_the_create_permission_may_create()
    {
        var (company, hr) = await SetupAsync();
        var manager = await ManagerAsync(company);
        var employee = await EmployeeAsync(company);
        var body = CreateBody(company.DepartmentId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, "/api/employees", null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(company, HttpMethod.Post, "/api/employees", manager.Token, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(company, HttpMethod.Post, "/api/employees", employee.Token, body)).StatusCode);
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.created"));
        Assert.Equal(HttpStatusCode.Created, (await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, body)).StatusCode);
    }

    [Fact]
    public async Task A_token_of_another_company_cannot_create_here()
    {
        var (company, _) = await SetupAsync();
        var (_, foreignHr) = await SetupAsync();
        var before = await CountsAsync(company.CompanyId);

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", foreignHr.Token, CreateBody(company.DepartmentId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    // ---------- rollback ----------

    [Fact]
    public async Task A_failing_audit_entry_rolls_back_the_account_and_the_employee_and_frees_the_code()
    {
        var (company, hr) = await SetupAsync();
        Switch.AuditStageFails = true;
        var before = await CountsAsync(company.CompanyId);

        var failed = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "rolled@example.test"));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var raw = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("simulated", raw);                         // generic body, no internal detail
        Assert.DoesNotContain("secret detail", raw);
        Assert.Equal(before, await CountsAsync(company.CompanyId));

        Switch.AuditStageFails = false;
        var retry = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "rolled@example.test"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("EMP-0001", (await JsonOf(retry)).GetProperty("empCode").GetString());     // the rolled-back attempt did not use up EMP-0001
    }

    [Fact]
    public async Task Created_employees_appear_only_in_their_own_company()
    {
        var (companyA, hrA) = await SetupAsync();
        var (companyB, _) = await SetupAsync();

        await CallAsync(companyA, HttpMethod.Post, "/api/employees", hrA.Token, CreateBody(companyA.DepartmentId));

        await using var db = Fx.CreateDbContext();
        Assert.Equal(1, await db.Employees.CountAsync(e => e.CompanyId == companyA.CompanyId && e.EmpCode == "EMP-0001"));
        Assert.Equal(0, await db.Employees.CountAsync(e => e.CompanyId == companyB.CompanyId && e.EmpCode == "EMP-0001"));
    }
}
