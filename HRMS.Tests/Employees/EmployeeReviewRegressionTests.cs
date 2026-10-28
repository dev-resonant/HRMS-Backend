using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Employees;

/// <summary>
/// Regression tests for defects found in the review of the employee module (see DEVELOPMENT_LOG Session 23). Each one was written
/// first and shown to fail against the code as it was.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmployeeReviewRegressionTests : EmployeeApiTestBase
{
    public EmployeeReviewRegressionTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    // ---------- paging arithmetic ----------

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1_000_000, 100)]
    [InlineData(2, 100)]
    public async Task A_page_far_beyond_the_data_returns_an_empty_page_not_an_error(int page, int pageSize)
    {
        var (company, hr) = await SetupAsync();
        await SeedManyAsync(company, 3);

        var response = await GetAsync(company, $"/api/employees?page={page}&pageSize={pageSize}", hr.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(4, body.GetProperty("totalCount").GetInt32());                 // 3 seeded + the HR user's own record
        Assert.Equal(page, body.GetProperty("page").GetInt32());
    }

    // ---------- NUL characters ----------

    [Theory]
    [InlineData("%00")]
    [InlineData("abc%00def")]
    public async Task A_search_text_containing_a_NUL_character_is_a_400_not_a_server_error(string encoded)
    {
        var (company, hr) = await SetupAsync();

        var response = await GetAsync(company, "/api/employees?search=" + encoded, hr.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("designation")]
    public async Task A_NUL_character_in_a_text_field_is_rejected_on_create_and_update(string field)
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var before = await CountsAsync(company.CompanyId);

        var create = new Dictionary<string, object?>
        {
            ["name"] = "Asha Rao", ["email"] = $"nul-{Guid.NewGuid():N}@example.test", ["departmentId"] = company.DepartmentId,
            ["designation"] = "Engineer", ["joiningDate"] = "2024-03-01"
        };
        create[field] = "bad\u0000value";
        var update = new Dictionary<string, object?>
        {
            ["name"] = "Tara Target", ["departmentId"] = company.DepartmentId, ["managerId"] = null, ["designation"] = "Engineer",
            ["joiningDate"] = "2024-01-15", ["status"] = "active", ["lastWorkingDate"] = null
        };
        update[field] = "bad\u0000value";

        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, create);
        var updated = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, update);

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    // ---------- account state is enforced independently of the session table ----------

    [Fact]
    public async Task A_session_row_that_exists_for_a_deactivated_user_still_cannot_use_any_protected_endpoint()
    {
        var (company, hr) = await SetupAsync();
        var victim = await HrAsync(company);                       // a second HR admin with every permission
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        Assert.Equal(HttpStatusCode.OK,
            (await CallAsync(company, HttpMethod.Put, $"/api/employees/{victim.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2024-12-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(company, "/api/employees", victim.Token)).StatusCode);      // the old token: session deleted

        // A login that raced with the deactivation can leave a valid, signed token with a fresh session behind. Account state is
        // checked again on every protected request, so it must still be useless.
        var raced = TestJwt.Create(Fx.JwtSecret, Factory.Issuer, Factory.Audience, victim.UserId, company.CompanyId, victim.Email, SystemRoleKeys.HrAdmin,
            expiresUtc: DateTime.UtcNow.AddHours(2));
        await TestData.AddSessionAsync(Fx, victim.UserId, raced, DateTime.UtcNow.AddHours(2));

        var update = new { name = "Hijacked", departmentId = company.DepartmentId, managerId = (Guid?)null, designation = "x", joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null };
        var attempts = new[]
        {
            await GetAsync(company, "/api/employees", raced),
            await GetAsync(company, $"/api/employees/{target.EmployeeId}", raced),
            await CallAsync(company, HttpMethod.Post, "/api/employees", raced, CreateBody(company.DepartmentId)),
            await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", raced, update),
            await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}/deactivate", raced, new { lastWorkingDate = "2024-12-31" }),
            await GetAsync(company, "/api/auth/me", raced)
        };

        Assert.All(attempts, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal("Tara Target", (await LoadAsync(target.EmployeeId)).User.Name);                 // nothing was changed
        Assert.True((await LoadAsync(target.EmployeeId)).User.IsActive);
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.created"));
        Assert.Equal(HttpStatusCode.OK, (await Factory.ClientFor(company.Domain).LogoutAsync(raced)).StatusCode);   // it can still end its own session
    }

    // ---------- a manager who was deactivated must not freeze their reports ----------

    [Fact]
    public async Task A_report_whose_manager_was_deactivated_can_still_be_edited_until_HR_reassigns_them()
    {
        var (company, hr) = await SetupAsync();
        var manager = await SeedAsync(company, "Mira Manager", "EMP-0001", SystemRoleKeys.Manager);
        var report = await SeedAsync(company, "Ravi Report", "EMP-0002", managerEmployeeId: manager.EmployeeId);
        Assert.Equal(HttpStatusCode.OK,
            (await CallAsync(company, HttpMethod.Put, $"/api/employees/{manager.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2024-12-31" })).StatusCode);

        object Body(string designation, Guid? managerId) => new
        {
            name = "Ravi Report", departmentId = company.DepartmentId, managerId, designation,
            joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null
        };

        // Unchanged (now inactive) manager: an unrelated edit must succeed.
        var edit = await CallAsync(company, HttpMethod.Put, $"/api/employees/{report.EmployeeId}", hr.Token, Body("Senior Engineer", manager.EmployeeId));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(manager.EmployeeId, (await LoadAsync(report.EmployeeId)).Employee.ManagerId);

        // Choosing the inactive manager for someone else is still refused, and clearing / replacing the manager works.
        var other = await SeedAsync(company, "Olga Other", "EMP-0003");
        var assignInactive = await CallAsync(company, HttpMethod.Put, $"/api/employees/{other.EmployeeId}", hr.Token,
            new { name = "Olga Other", departmentId = company.DepartmentId, managerId = manager.EmployeeId, designation = "Engineer", joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null });
        var clear = await CallAsync(company, HttpMethod.Put, $"/api/employees/{report.EmployeeId}", hr.Token, Body("Senior Engineer", null));
        Assert.Equal(HttpStatusCode.BadRequest, assignInactive.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await LoadAsync(report.EmployeeId)).Employee.ManagerId);
    }
}
