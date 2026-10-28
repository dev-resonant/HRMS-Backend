using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;
using HRMS.Tests.Employees;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Departments;

/// <summary>
/// Departments (specification 5.3, Table 5, API table): real API, real PostgreSQL (guarded Testcontainers), real pipeline.
/// DELETE and bulk reassignment are covered by DepartmentDeleteAndReassignTests.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DepartmentApiTests : EmployeeApiTestBase
{
    public DepartmentApiTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private Task<HttpResponseMessage> PostDepartmentAsync(AuthzCompany company, string? token, object body)
        => CallAsync(company, HttpMethod.Post, "/api/departments", token, body);

    private Task<HttpResponseMessage> PutDepartmentAsync(AuthzCompany company, string? token, Guid id, object body)
        => CallAsync(company, HttpMethod.Put, $"/api/departments/{id}", token, body);

    private async Task<Guid> CreateAsync(AuthzCompany company, AuthzUser hr, string name, Guid? parent = null, Guid? head = null)
    {
        var response = await PostDepartmentAsync(company, hr.Token, new { name, parentDepartmentId = parent, headEmployeeId = head });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<Department> LoadDepartmentAsync(Guid id)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Departments.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<int> DepartmentCountAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Departments.CountAsync(d => d.CompanyId == companyId);
    }

    private static JsonElement Prop(JsonElement element, string name)
        => element.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static Guid? GuidOrNull(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetGuid();

    // ================= create =================

    [Fact]
    public async Task HR_creates_a_top_level_department_in_its_own_company_and_it_is_audited_exactly()
    {
        var (company, hr) = await SetupAsync();

        var response = await PostDepartmentAsync(company, hr.Token, new { name = "  Finance  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/departments/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("Finance", body.GetProperty("name").GetString());                     // trimmed
        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentDepartmentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("headEmployeeId").ValueKind);
        Assert.Equal(0, body.GetProperty("employeeCount").GetInt32());
        Assert.Equal(0, body.GetProperty("childDepartmentCount").GetInt32());
        Assert.DoesNotContain(body.EnumerateObject(), p => p.Name.Contains("company", StringComparison.OrdinalIgnoreCase));

        var row = await LoadDepartmentAsync(id);
        Assert.Equal(company.CompanyId, row.CompanyId);

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "department.created"));
        Assert.Equal("department", audit.Module);
        Assert.Equal(id, audit.TargetId);
        Assert.Equal(hr.UserId, audit.UserId);
        Assert.Equal(company.CompanyId, audit.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, audit.IpAddress);
        Assert.Null(audit.OldValue);
        using var after = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal("Finance", Prop(after.RootElement, "name").GetString());
        Assert.Equal(JsonValueKind.Null, Prop(after.RootElement, "parentDeptId").ValueKind);
        Assert.Equal(JsonValueKind.Null, Prop(after.RootElement, "headEmpId").ValueKind);
    }

    [Fact]
    public async Task A_department_can_be_nested_under_a_parent_and_have_a_head()
    {
        var (company, hr) = await SetupAsync();
        var head = await SeedAsync(company, "Hannah Head", "EMP-0100");
        var engineering = await CreateAsync(company, hr, "Engineering");

        var response = await PostDepartmentAsync(company, hr.Token, new { name = "Frontend", parentDepartmentId = engineering, headEmployeeId = head.EmployeeId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(engineering, body.GetProperty("parentDepartmentId").GetGuid());
        Assert.Equal("Engineering", body.GetProperty("parentDepartmentName").GetString());
        Assert.Equal(head.EmployeeId, body.GetProperty("headEmployeeId").GetGuid());
        Assert.Equal("Hannah Head", body.GetProperty("headEmployeeName").GetString());
        var parent = await JsonOf(await GetAsync(company, $"/api/departments/{engineering}", hr.Token));
        Assert.Equal(1, parent.GetProperty("childDepartmentCount").GetInt32());

        using var after = JsonDocument.Parse(Assert.Single(await AuditAsync(company.CompanyId, "department.created"), a => a.TargetId == body.GetProperty("id").GetGuid()).NewValue!);
        Assert.Equal(engineering, Prop(after.RootElement, "parentDeptId").GetGuid());
        Assert.Equal(head.EmployeeId, Prop(after.RootElement, "headEmpId").GetGuid());
    }

    [Fact]
    public async Task Nesting_depth_is_not_limited()
    {
        var (company, hr) = await SetupAsync();
        Guid? parent = null;

        for (var depth = 0; depth < 40; depth++)
        {
            parent = await CreateAsync(company, hr, $"Level {depth}", parent);
        }

        Assert.Equal(40 + 1, await DepartmentCountAsync(company.CompanyId));                  // +1: the company's seeded department
    }

    [Fact]
    public async Task Names_are_validated_and_a_rejected_request_creates_nothing()
    {
        var (company, hr) = await SetupAsync();
        var departmentsBefore = await DepartmentCountAsync(company.CompanyId);
        var auditBefore = (await AuditAsync(company.CompanyId)).Count;

        foreach (var bad in new object[]
        {
            new { name = "" }, new { name = "   " }, new { name = new string('x', 101) }, new { name = "bad\0name" },
            new { name = "Ok", parentDepartmentId = Guid.Empty }, new { name = "Ok", headEmployeeId = Guid.Empty }
        })
        {
            var response = await PostDepartmentAsync(company, hr.Token, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(company, HttpMethod.Post, "/api/departments", hr.Token, new { name = (string?)null })).StatusCode);
        Assert.Equal(departmentsBefore, await DepartmentCountAsync(company.CompanyId));
        Assert.Equal(auditBefore, (await AuditAsync(company.CompanyId)).Count);
    }

    [Fact]
    public async Task A_name_of_exactly_100_characters_and_repeated_names_are_accepted_because_the_specification_defines_no_uniqueness()
    {
        var (company, hr) = await SetupAsync();

        await CreateAsync(company, hr, new string('n', 100));
        await CreateAsync(company, hr, "Operations");
        await CreateAsync(company, hr, "Operations");                                          // same name, even under the same parent (none)

        await using var db = Fx.CreateDbContext();
        Assert.Equal(2, await db.Departments.CountAsync(d => d.CompanyId == company.CompanyId && d.Name == "Operations"));
    }

    [Fact]
    public async Task The_company_of_a_new_department_comes_from_the_caller_never_from_the_body()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();

        var response = await PostDepartmentAsync(company, hr.Token, new Dictionary<string, object> { ["name"] = "Spoof", ["companyId"] = other.CompanyId, ["CompanyId"] = other.CompanyId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = await LoadDepartmentAsync((await JsonOf(response)).GetProperty("id").GetGuid());
        Assert.Equal(company.CompanyId, row.CompanyId);
        await using var db = Fx.CreateDbContext();
        Assert.Equal(0, await db.Departments.CountAsync(d => d.CompanyId == other.CompanyId && d.Name == "Spoof"));
    }

    [Fact]
    public async Task A_parent_or_head_from_another_company_is_refused_exactly_like_one_that_does_not_exist()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var foreignDepartment = await CreateAsync(other, otherHr, "Foreign");
        var foreignEmployee = await SeedAsync(other, "Foreign Person", "EMP-0900");
        var before = await DepartmentCountAsync(company.CompanyId);

        var foreignParent = await PostDepartmentAsync(company, hr.Token, new { name = "X", parentDepartmentId = foreignDepartment });
        var missingParent = await PostDepartmentAsync(company, hr.Token, new { name = "X", parentDepartmentId = Guid.NewGuid() });
        var foreignHead = await PostDepartmentAsync(company, hr.Token, new { name = "X", headEmployeeId = foreignEmployee.EmployeeId });
        var missingHead = await PostDepartmentAsync(company, hr.Token, new { name = "X", headEmployeeId = Guid.NewGuid() });

        Assert.All(new[] { foreignParent, missingParent, foreignHead, missingHead }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(await missingParent.Content.ReadAsStringAsync(), await foreignParent.Content.ReadAsStringAsync());
        Assert.Equal(await missingHead.Content.ReadAsStringAsync(), await foreignHead.Content.ReadAsStringAsync());
        Assert.Equal(before, await DepartmentCountAsync(company.CompanyId));
        Assert.Equal(0, await DepartmentCountAsyncByParent(foreignDepartment));
    }

    private async Task<int> DepartmentCountAsyncByParent(Guid parent)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Departments.CountAsync(d => d.ParentDeptId == parent);
    }

    [Fact]
    public async Task An_inactive_employee_cannot_become_a_head_but_the_head_need_not_belong_to_the_department()
    {
        var (company, hr) = await SetupAsync();
        var gone = await SeedAsync(company, "Gone Person", "EMP-0200", status: EmployeeStatus.Inactive, userActive: false);
        var elsewhere = await SeedAsync(company, "Other Dept Person", "EMP-0201", departmentId: await AddDepartmentAsync(company.CompanyId, "Sales"));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostDepartmentAsync(company, hr.Token, new { name = "A", headEmployeeId = gone.EmployeeId })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostDepartmentAsync(company, hr.Token, new { name = "B", headEmployeeId = elsewhere.EmployeeId })).StatusCode);
    }

    [Fact]
    public async Task A_creation_whose_audit_entry_fails_leaves_nothing_behind()
    {
        var (company, hr) = await SetupAsync();
        var before = await DepartmentCountAsync(company.CompanyId);
        Switch.AuditStageFails = true;

        var response = await PostDepartmentAsync(company, hr.Token, new { name = "Ghost" });
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await DepartmentCountAsync(company.CompanyId));
        Assert.Empty(await AuditAsync(company.CompanyId, "department.created"));
        Assert.DoesNotContain("simulated", await response.Content.ReadAsStringAsync());
    }

    // ================= update =================

    [Fact]
    public async Task Update_replaces_name_parent_and_head_clears_them_with_null_and_audits_before_and_after()
    {
        var (company, hr) = await SetupAsync();
        var head = await SeedAsync(company, "Hannah Head", "EMP-0300");
        var parent = await CreateAsync(company, hr, "Engineering");
        var id = await CreateAsync(company, hr, "Frontend");

        var response = await PutDepartmentAsync(company, hr.Token, id, new { name = " Web ", parentDepartmentId = parent, headEmployeeId = head.EmployeeId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("Web", body.GetProperty("name").GetString());
        Assert.Equal(parent, body.GetProperty("parentDepartmentId").GetGuid());
        Assert.Equal(head.EmployeeId, body.GetProperty("headEmployeeId").GetGuid());
        var audit = Assert.Single(await AuditAsync(company.CompanyId, "department.updated"));
        Assert.Equal(id, audit.TargetId);
        Assert.Equal(hr.UserId, audit.UserId);
        using (var old = JsonDocument.Parse(audit.OldValue!))
        using (var now = JsonDocument.Parse(audit.NewValue!))
        {
            Assert.Equal("Frontend", Prop(old.RootElement, "name").GetString());
            Assert.Equal(JsonValueKind.Null, Prop(old.RootElement, "parentDeptId").ValueKind);
            Assert.Equal("Web", Prop(now.RootElement, "name").GetString());
            Assert.Equal(parent, Prop(now.RootElement, "parentDeptId").GetGuid());
            Assert.Equal(head.EmployeeId, Prop(now.RootElement, "headEmpId").GetGuid());
        }

        var cleared = await PutDepartmentAsync(company, hr.Token, id, new { name = "Web", parentDepartmentId = (Guid?)null, headEmployeeId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var row = await LoadDepartmentAsync(id);
        Assert.Null(row.ParentDeptId);
        Assert.Null(row.HeadEmpId);
        Assert.Equal(2, (await AuditAsync(company.CompanyId, "department.updated")).Count);
    }

    [Fact]
    public async Task Update_of_an_unknown_department_is_404_and_invalid_input_changes_nothing()
    {
        var (company, hr) = await SetupAsync();
        var id = await CreateAsync(company, hr, "Stable");

        Assert.Equal(HttpStatusCode.NotFound, (await PutDepartmentAsync(company, hr.Token, Guid.NewGuid(), new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutDepartmentAsync(company, hr.Token, id, new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutDepartmentAsync(company, hr.Token, id, new { name = new string('x', 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutDepartmentAsync(company, hr.Token, id, new { name = "X", parentDepartmentId = Guid.NewGuid() })).StatusCode);

        Assert.Equal("Stable", (await LoadDepartmentAsync(id)).Name);
        Assert.Empty(await AuditAsync(company.CompanyId, "department.updated"));
    }

    [Fact]
    public async Task A_department_cannot_become_its_own_parent_or_a_child_of_its_own_descendants()
    {
        var (company, hr) = await SetupAsync();
        var a = await CreateAsync(company, hr, "A");
        var b = await CreateAsync(company, hr, "B", a);
        var c = await CreateAsync(company, hr, "C", b);

        var self = await PutDepartmentAsync(company, hr.Token, a, new { name = "A", parentDepartmentId = a });
        var child = await PutDepartmentAsync(company, hr.Token, a, new { name = "A", parentDepartmentId = b });
        var grandchild = await PutDepartmentAsync(company, hr.Token, a, new { name = "A", parentDepartmentId = c });

        Assert.All(new[] { self, child, grandchild }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Null((await LoadDepartmentAsync(a)).ParentDeptId);

        // Moving a subtree elsewhere is fine (c under a: shorter chain, no cycle).
        Assert.Equal(HttpStatusCode.OK, (await PutDepartmentAsync(company, hr.Token, c, new { name = "C", parentDepartmentId = a })).StatusCode);
    }

    [Fact]
    public async Task Update_keeps_an_offboarded_head_but_will_not_assign_a_new_inactive_one()
    {
        var (company, hr) = await SetupAsync();
        var head = await SeedAsync(company, "Soon Gone", "EMP-0400");
        var id = await CreateAsync(company, hr, "Support", head: head.EmployeeId);
        await using (var db = Fx.CreateDbContext())
        {
            await db.Employees.Where(e => e.Id == head.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EmployeeStatus.Inactive));
        }

        var rename = await PutDepartmentAsync(company, hr.Token, id, new { name = "Support Desk", headEmployeeId = head.EmployeeId });
        var other = await SeedAsync(company, "Also Gone", "EMP-0401", status: EmployeeStatus.Inactive, userActive: false);
        var reassign = await PutDepartmentAsync(company, hr.Token, id, new { name = "Support Desk", headEmployeeId = other.EmployeeId });

        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reassign.StatusCode);
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_update_back()
    {
        var (company, hr) = await SetupAsync();
        var id = await CreateAsync(company, hr, "Before");
        Switch.AuditStageFails = true;

        var response = await PutDepartmentAsync(company, hr.Token, id, new { name = "After" });
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Before", (await LoadDepartmentAsync(id)).Name);
        Assert.Empty(await AuditAsync(company.CompanyId, "department.updated"));
    }

    // ================= reads and scope =================

    [Fact]
    public async Task HR_lists_every_department_of_its_own_company_only_ordered_by_name_with_counts()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        await CreateAsync(other, otherHr, "Foreign Dept");
        var zeta = await CreateAsync(company, hr, "Zeta");
        await CreateAsync(company, hr, "Alpha", zeta);
        await SeedAsync(company, "Zed One", "EMP-0500", departmentId: zeta);
        await SeedAsync(company, "Zed Two", "EMP-0501", departmentId: zeta, status: EmployeeStatus.Inactive, userActive: false);

        var response = await GetAsync(company, "/api/departments", hr.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await JsonOf(response)).EnumerateArray().ToList();
        Assert.Equal(new[] { "Alpha", "Engineering", "Zeta" }, items.Select(i => i.GetProperty("name").GetString()));   // "Engineering" is the company's seeded department
        Assert.DoesNotContain(items, i => i.GetProperty("name").GetString() == "Foreign Dept");
        var zetaItem = items.Single(i => i.GetProperty("name").GetString() == "Zeta");
        Assert.Equal(2, zetaItem.GetProperty("employeeCount").GetInt32());                  // every assigned record, whatever its status
        Assert.Equal(1, zetaItem.GetProperty("childDepartmentCount").GetInt32());
    }

    [Fact]
    public async Task A_manager_and_an_employee_see_only_their_own_department()
    {
        var (company, hr) = await SetupAsync();
        var sales = await AddDepartmentAsync(company.CompanyId, "Sales");
        var manager = await AuthzTestData.AddUserAsync(Fx, Factory, company, AuthzTestData.SystemRoleId(SystemRoleKeys.Manager), SystemRoleKeys.Manager, departmentId: sales);
        var employee = await AuthzTestData.AddUserAsync(Fx, Factory, company, AuthzTestData.SystemRoleId(SystemRoleKeys.Employee), SystemRoleKeys.Employee);   // seeded department

        var managerList = (await JsonOf(await GetAsync(company, "/api/departments", manager.Token))).EnumerateArray().ToList();
        var employeeList = (await JsonOf(await GetAsync(company, "/api/departments", employee.Token))).EnumerateArray().ToList();

        Assert.Equal(new[] { sales }, managerList.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { company.DepartmentId }, employeeList.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(company, $"/api/departments/{sales}", manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(company, $"/api/departments/{company.DepartmentId}", employee.Token)).StatusCode);
    }

    [Fact]
    public async Task Another_department_of_the_same_company_and_a_foreign_one_look_exactly_like_a_missing_one()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var sales = await CreateAsync(company, hr, "Sales");
        var foreign = await CreateAsync(other, otherHr, "Foreign");
        var employee = await EmployeeAsync(company);

        var sameCompany = await GetAsync(company, $"/api/departments/{sales}", employee.Token);
        var foreignAsEmployee = await GetAsync(company, $"/api/departments/{foreign}", employee.Token);
        var foreignAsHr = await GetAsync(company, $"/api/departments/{foreign}", hr.Token);
        var missing = await GetAsync(company, $"/api/departments/{Guid.NewGuid()}", hr.Token);

        Assert.All(new[] { sameCompany, foreignAsEmployee, foreignAsHr, missing }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var expected = await missing.Content.ReadAsStringAsync();
        Assert.Equal(expected, await sameCompany.Content.ReadAsStringAsync());
        Assert.Equal(expected, await foreignAsEmployee.Content.ReadAsStringAsync());
        Assert.Equal(expected, await foreignAsHr.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Foreign", expected);
    }

    [Fact]
    public async Task An_account_without_an_employee_record_and_only_the_own_department_permission_sees_nothing()
    {
        var (company, _) = await SetupAsync();
        var noRecord = await AuthzTestData.AddSystemUserAsync(Fx, Factory, company, SystemRoleKeys.Employee, withEmployee: false);

        var list = await GetAsync(company, "/api/departments", noRecord.Token);
        var one = await GetAsync(company, $"/api/departments/{company.DepartmentId}", noRecord.Token);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await JsonOf(list)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
    }

    // ================= authorization =================

    [Fact]
    public async Task Every_endpoint_requires_authentication()
    {
        var (company, _) = await SetupAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(company, "/api/departments", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(company, $"/api/departments/{company.DepartmentId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostDepartmentAsync(company, null, new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutDepartmentAsync(company, null, company.DepartmentId, new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Only_HR_may_create_and_update_and_a_refused_attempt_changes_nothing()
    {
        var (company, hr) = await SetupAsync();
        var id = await CreateAsync(company, hr, "Protected");
        var manager = await ManagerAsync(company);
        var employee = await EmployeeAsync(company);
        var before = await DepartmentCountAsync(company.CompanyId);

        foreach (var user in new[] { manager, employee })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await PostDepartmentAsync(company, user.Token, new { name = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutDepartmentAsync(company, user.Token, id, new { name = "Hijacked" })).StatusCode);
        }

        Assert.Equal(before, await DepartmentCountAsync(company.CompanyId));
        Assert.Equal("Protected", (await LoadDepartmentAsync(id)).Name);
        Assert.Empty(await AuditAsync(company.CompanyId, "department.updated"));
    }

    [Fact]
    public async Task A_role_without_department_permissions_is_refused_everywhere_and_view_all_alone_fails_closed()
    {
        var (company, _) = await SetupAsync();
        var none = await AuthzTestData.AddUserAsync(Fx, Factory, company, await AuthzTestData.CreateCompanyRoleAsync(Fx, company.CompanyId, "{}"), "custom");
        var viewAllOnly = await AuthzTestData.AddUserAsync(Fx, Factory, company,
            await AuthzTestData.CreateCompanyRoleAsync(Fx, company.CompanyId, "{\"departments\":[\"view_all\"]}"), "custom");
        var manageOnly = await AuthzTestData.AddUserAsync(Fx, Factory, company,
            await AuthzTestData.CreateCompanyRoleAsync(Fx, company.CompanyId, "{\"departments\":[\"manage\"]}"), "custom");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/departments", none.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostDepartmentAsync(company, none.Token, new { name = "X" })).StatusCode);
        // GET requires view_own_department (every approved system role holding view_all also holds it); a custom role with view_all
        // alone is denied rather than guessed at.
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/departments", viewAllOnly.Token)).StatusCode);
        // manage without a read permission may write but not read.
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/departments", manageOnly.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostDepartmentAsync(company, manageOnly.Token, new { name = "Managed" })).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_and_a_token_of_another_company_are_refused()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == hr.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await PostDepartmentAsync(company, hr.Token, new { name = "X" })).StatusCode);
        // otherHr's token is valid for company "other" only; presented on this company's host it is a tenant mismatch.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostDepartmentAsync(company, otherHr.Token, new { name = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/departments", otherHr.Token)).StatusCode);
        var names = await LoadAllNamesAsync(company.CompanyId);
        Assert.DoesNotContain("X", names);
        Assert.DoesNotContain("Sneaky", names);
    }

    private async Task<List<string>> LoadAllNamesAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Departments.Where(d => d.CompanyId == companyId).Select(d => d.Name).ToListAsync();
    }

    [Fact]
    public async Task HR_of_another_company_cannot_read_change_or_attach_to_a_department()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var mine = await CreateAsync(company, hr, "Mine");

        var read = await GetAsync(other, $"/api/departments/{mine}", otherHr.Token);
        var change = await PutDepartmentAsync(other, otherHr.Token, mine, new { name = "Taken over" });
        var attach = await PostDepartmentAsync(other, otherHr.Token, new { name = "Child of mine", parentDepartmentId = mine });
        var reparent = await PutDepartmentAsync(other, otherHr.Token, await CreateAsync(other, otherHr, "Theirs"), new { name = "Theirs", parentDepartmentId = mine });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, change.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reparent.StatusCode);
        Assert.Equal("Mine", (await LoadDepartmentAsync(mine)).Name);
        Assert.Equal(0, await DepartmentCountAsyncByParent(mine));
    }

    // ================= concurrency =================

    [Fact]
    public async Task Opposing_re_parentings_never_create_a_cycle()
    {
        var (company, hr) = await SetupAsync();

        for (var round = 0; round < 6; round++)
        {
            var a = await CreateAsync(company, hr, $"A{round}");
            var b = await CreateAsync(company, hr, $"B{round}");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = new[]
            {
                Task.Run(async () => { await gate.Task; return await PutDepartmentAsync(company, hr.Token, a, new { name = "A", parentDepartmentId = b }); }),
                Task.Run(async () => { await gate.Task; return await PutDepartmentAsync(company, hr.Token, b, new { name = "B", parentDepartmentId = a }); })
            };
            gate.SetResult();
            var responses = await Task.WhenAll(tasks);

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
            var (parentOfA, parentOfB) = ((await LoadDepartmentAsync(a)).ParentDeptId, (await LoadDepartmentAsync(b)).ParentDeptId);
            Assert.False(parentOfA == b && parentOfB == a, "a two-department cycle was created");
        }
    }

    [Fact]
    public async Task A_re_parenting_waits_for_the_companys_hierarchy_lock_and_other_companies_are_not_blocked()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var id = await CreateAsync(company, hr, "Locked");
        var otherId = await CreateAsync(other, otherHr, "Free");

        // Another session holds the company's hierarchy lock (same key the application takes).
        await using var holder = new Npgsql.NpgsqlConnection(Fx.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        var companyKey = BitConverter.ToInt32(company.CompanyId.ToByteArray(), 0);
        await using (var command = new Npgsql.NpgsqlCommand($"select pg_advisory_xact_lock({0x44455054}, {companyKey})", holder, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var blocked = PutDepartmentAsync(company, hr.Token, id, new { name = "Renamed" });
        var unrelated = await PutDepartmentAsync(other, otherHr.Token, otherId, new { name = "Renamed elsewhere" });

        Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);                              // another company is unaffected
        await Task.Delay(1500);
        Assert.False(blocked.IsCompleted, "the update must wait for the hierarchy lock");
        Assert.Equal("Locked", (await LoadDepartmentAsync(id)).Name);

        await transaction.CommitAsync();                                                    // release

        Assert.Equal(HttpStatusCode.OK, (await blocked).StatusCode);
        Assert.Equal("Renamed", (await LoadDepartmentAsync(id)).Name);
    }

    [Fact]
    public async Task Parallel_creations_under_one_parent_all_succeed()
    {
        var (company, hr) = await SetupAsync();
        var parent = await CreateAsync(company, hr, "Root");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            await gate.Task;
            return await PostDepartmentAsync(company, hr.Token, new { name = $"Child {i}", parentDepartmentId = parent });
        }).ToArray();
        gate.SetResult();

        Assert.All(await Task.WhenAll(tasks), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(8, await DepartmentCountAsyncByParent(parent));
        Assert.Equal(9, (await AuditAsync(company.CompanyId, "department.created")).Count);   // root + 8
    }

    // ================= integration with employees and the contract =================

    [Fact]
    public async Task A_new_department_can_be_used_for_employees_and_a_rename_shows_in_the_employee_view()
    {
        var (company, hr) = await SetupAsync();
        var id = await CreateAsync(company, hr, "Legal");

        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(id, email: "legal.person@example.test"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var employeeId = (await JsonOf(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PutDepartmentAsync(company, hr.Token, id, new { name = "Legal & Compliance" })).StatusCode);

        var employee = await JsonOf(await GetAsync(company, $"/api/employees/{employeeId}", hr.Token));
        Assert.Equal("Legal & Compliance", employee.GetProperty("departmentName").GetString());
        var department = await JsonOf(await GetAsync(company, $"/api/departments/{id}", hr.Token));
        Assert.Equal(1, department.GetProperty("employeeCount").GetInt32());
    }

    [Fact]
    public async Task The_swagger_document_exposes_the_department_endpoints_with_dto_contracts_only()
    {
        var (company, _) = await SetupAsync();

        var response = await GetAsync(company, "/swagger/v1/swagger.json", null);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(paths.GetProperty("/api/departments").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/departments").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/departments/{id}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/departments/{id}").TryGetProperty("put", out _));
        Assert.True(paths.GetProperty("/api/departments/{id}").TryGetProperty("delete", out _));
        Assert.True(paths.GetProperty("/api/departments/{id}/reassign-employees").TryGetProperty("post", out _));
        Assert.Equal(new[] { "headEmployeeId", "name", "parentDepartmentId" },
            schemas.GetProperty("CreateDepartmentRequest").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(new[] { "headEmployeeId", "name", "parentDepartmentId" },
            schemas.GetProperty("UpdateDepartmentRequest").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var dto = schemas.GetProperty("DepartmentDto").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(dto, n => n.Contains("company", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("childDepartmentCount", dto);
        Assert.DoesNotContain(schemas.EnumerateObject(), s => s.Name is "Department" or "Company" or "Employee");   // no entity is exposed
    }
}
