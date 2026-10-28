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
/// DELETE /api/departments/{id} (only an empty department with no sub-departments) and POST /api/departments/{id}/reassign-employees
/// (all-or-nothing bulk move). Real API, real PostgreSQL (guarded Testcontainers). The races are proven with a second connection that
/// holds an uncommitted change while the request runs, so the outcome does not depend on timing luck.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DepartmentDeleteAndReassignTests : EmployeeApiTestBase
{
    public DepartmentDeleteAndReassignTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private async Task<Guid> NewDepartmentAsync(AuthzCompany company, AuthzUser hr, string name, Guid? parent = null)
    {
        var response = await CallAsync(company, HttpMethod.Post, "/api/departments", hr.Token, new { name, parentDepartmentId = parent });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> DeleteAsync(AuthzCompany company, string? token, Guid id)
        => CallAsync(company, HttpMethod.Delete, $"/api/departments/{id}", token);

    private Task<HttpResponseMessage> ReassignAsync(AuthzCompany company, string? token, Guid source, object body)
        => CallAsync(company, HttpMethod.Post, $"/api/departments/{source}/reassign-employees", token, body);

    private async Task<bool> ExistsAsync(Guid departmentId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Departments.AnyAsync(d => d.Id == departmentId);
    }

    private async Task<Dictionary<Guid, Guid>> DepartmentsOfAsync(IEnumerable<Guid> employeeIds)
    {
        var ids = employeeIds.ToList();
        await using var db = Fx.CreateDbContext();
        return await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DepartmentId);
    }

    private async Task<List<Guid>> SeedInAsync(AuthzCompany company, Guid department, int count, int firstNumber, EmployeeStatus status = EmployeeStatus.Active)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var seeded = await SeedAsync(company, $"Person {firstNumber + i}", $"EMP-{firstNumber + i:D4}", departmentId: department, status: status, userActive: status != EmployeeStatus.Inactive);
            ids.Add(seeded.EmployeeId);
        }

        return ids;
    }

    private static JsonElement Prop(JsonElement element, string name)
        => element.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    // ================= delete =================

    [Fact]
    public async Task An_empty_department_without_children_is_deleted_and_audited_exactly()
    {
        var (company, hr) = await SetupAsync();
        var head = await SeedAsync(company, "Head Person", "EMP-0800");
        var parent = await NewDepartmentAsync(company, hr, "Parent");
        var id = (await JsonOf(await CallAsync(company, HttpMethod.Post, "/api/departments", hr.Token, new { name = "Temp", parentDepartmentId = parent, headEmployeeId = head.EmployeeId }))).GetProperty("id").GetGuid();

        var response = await DeleteAsync(company, hr.Token, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.False(await ExistsAsync(id));
        Assert.True(await ExistsAsync(parent));                                           // the parent is untouched
        await using (var db = Fx.CreateDbContext())
        {
            Assert.True(await db.Employees.AnyAsync(e => e.Id == head.EmployeeId));       // the former head is untouched
        }

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "department.deleted"));
        Assert.Equal("department", audit.Module);
        Assert.Equal(id, audit.TargetId);
        Assert.Equal(hr.UserId, audit.UserId);
        Assert.Equal(company.CompanyId, audit.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, audit.IpAddress);
        Assert.Null(audit.NewValue);
        using var old = JsonDocument.Parse(audit.OldValue!);
        Assert.Equal("Temp", Prop(old.RootElement, "name").GetString());
        Assert.Equal(parent, Prop(old.RootElement, "parentDeptId").GetGuid());
        Assert.Equal(head.EmployeeId, Prop(old.RootElement, "headEmpId").GetGuid());
    }

    [Fact]
    public async Task A_department_with_employees_cannot_be_deleted_whatever_their_status_and_nothing_is_moved()
    {
        var (company, hr) = await SetupAsync();
        var id = await NewDepartmentAsync(company, hr, "Busy");
        var active = await SeedInAsync(company, id, 1, 810);
        var offboarded = await SeedInAsync(company, id, 1, 811, EmployeeStatus.Inactive);

        var response = await DeleteAsync(company, hr.Token, id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot be deleted", await response.Content.ReadAsStringAsync());
        Assert.True(await ExistsAsync(id));
        Assert.Equal(new[] { id, id }, (await DepartmentsOfAsync(active.Concat(offboarded))).Values);   // not moved, not deleted
        Assert.Empty(await AuditAsync(company.CompanyId, "department.deleted"));                  // no misleading success entry
    }

    [Fact]
    public async Task A_department_with_sub_departments_cannot_be_deleted_until_they_are_gone()
    {
        var (company, hr) = await SetupAsync();
        var parent = await NewDepartmentAsync(company, hr, "Parent");
        var child = await NewDepartmentAsync(company, hr, "Child", parent);

        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(company, hr.Token, parent)).StatusCode);
        Assert.True(await ExistsAsync(parent));
        Assert.True(await ExistsAsync(child));                                                    // never cascaded
        Assert.Empty(await AuditAsync(company.CompanyId, "department.deleted"));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(company, hr.Token, child)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(company, hr.Token, parent)).StatusCode);
        Assert.Equal(2, (await AuditAsync(company.CompanyId, "department.deleted")).Count);
    }

    [Fact]
    public async Task A_missing_or_foreign_department_is_404_with_identical_answers_and_a_second_delete_is_404()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var foreign = await NewDepartmentAsync(other, otherHr, "Foreign Secret");
        var mine = await NewDepartmentAsync(company, hr, "Mine");

        var missing = await DeleteAsync(company, hr.Token, Guid.NewGuid());
        var foreignResponse = await DeleteAsync(company, hr.Token, foreign);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreignResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Foreign", await foreignResponse.Content.ReadAsStringAsync());
        Assert.True(await ExistsAsync(foreign));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(company, hr.Token, mine)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(company, hr.Token, mine)).StatusCode);
        Assert.Single(await AuditAsync(company.CompanyId, "department.deleted"));
    }

    [Fact]
    public async Task Deleting_needs_authentication_and_the_manage_permission()
    {
        var (company, hr) = await SetupAsync();
        var id = await NewDepartmentAsync(company, hr, "Protected");
        var manager = await ManagerAsync(company);
        var employee = await EmployeeAsync(company);
        var readOnly = await AuthzTestData.AddUserAsync(Fx, Factory, company,
            await AuthzTestData.CreateCompanyRoleAsync(Fx, company.CompanyId, "{\"departments\":[\"view_all\",\"view_own_department\"]}"), "custom");

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(company, null, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(company, manager.Token, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(company, employee.Token, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(company, readOnly.Token, id)).StatusCode);

        Assert.True(await ExistsAsync(id));
        Assert.Empty(await AuditAsync(company.CompanyId, "department.deleted"));
    }

    [Fact]
    public async Task A_failing_audit_entry_leaves_the_department_in_place()
    {
        var (company, hr) = await SetupAsync();
        var id = await NewDepartmentAsync(company, hr, "Stays");
        Switch.AuditStageFails = true;

        var response = await DeleteAsync(company, hr.Token, id);
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(await ExistsAsync(id));
        Assert.Empty(await AuditAsync(company.CompanyId, "department.deleted"));
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("child")]
    public async Task The_database_stops_a_delete_that_races_with_an_assignment_or_a_new_child(string racer)
    {
        var (company, hr) = await SetupAsync();
        var id = await NewDepartmentAsync(company, hr, "Raced");
        Guid insertedId;

        // Another session has inserted a row that references the department but has not committed. The request cannot see it, so its
        // counts are zero; only the foreign key can protect the invariant.
        await using var other = Fx.CreateDbContext();
        await using var transaction = await other.Database.BeginTransactionAsync();

        if (racer == "employee")
        {
            var user = new User
            {
                Id = Guid.NewGuid(), CompanyId = company.CompanyId, RoleId = AuthzTestData.SystemRoleId(SystemRoleKeys.Employee),
                Name = "Racer", Email = $"racer-{Guid.NewGuid():N}@example.test", PasswordHash = "x", IsActive = true
            };
            var employee = new Employee
            {
                Id = Guid.NewGuid(), UserId = user.Id, CompanyId = company.CompanyId, DepartmentId = id, EmpCode = "EMP-7001",
                Designation = "Engineer", JoiningDate = new DateOnly(2024, 1, 1), Status = EmployeeStatus.Active
            };
            other.Users.Add(user);
            other.Employees.Add(employee);
            insertedId = employee.Id;
        }
        else
        {
            var child = new Department { Id = Guid.NewGuid(), CompanyId = company.CompanyId, Name = "Late child", ParentDeptId = id };
            other.Departments.Add(child);
            insertedId = child.Id;
        }

        await other.SaveChangesAsync();                                                          // uncommitted

        var delete = DeleteAsync(company, hr.Token, id);
        await Task.Delay(1500);
        Assert.False(delete.IsCompleted, "the delete must wait for the uncommitted row that references the department");

        await transaction.CommitAsync();

        var response = await delete;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot be deleted", await response.Content.ReadAsStringAsync());
        Assert.True(await ExistsAsync(id));
        await using var db = Fx.CreateDbContext();
        Assert.True(racer == "employee" ? await db.Employees.AnyAsync(e => e.Id == insertedId) : await db.Departments.AnyAsync(d => d.Id == insertedId));   // the racer's row survived
        Assert.Empty(await AuditAsync(company.CompanyId, "department.deleted"));                 // rolled back with the delete
    }

    [Fact]
    public async Task A_delete_racing_with_a_reassignment_into_the_department_never_orphans_an_employee()
    {
        var (company, hr) = await SetupAsync();

        for (var round = 0; round < 5; round++)
        {
            var target = await NewDepartmentAsync(company, hr, $"Target {round}");
            var source = await NewDepartmentAsync(company, hr, $"Source {round}");
            var moved = await SeedInAsync(company, source, 1, 900 + round * 2);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var delete = Task.Run(async () => { await gate.Task; return await DeleteAsync(company, hr.Token, target); });
            var move = Task.Run(async () => { await gate.Task; return await ReassignAsync(company, hr.Token, source, new { toDepartmentId = target, employeeIds = moved }); });
            gate.SetResult();
            var (deleted, reassigned) = (await delete, await move);

            Assert.False(deleted.StatusCode == HttpStatusCode.NoContent && reassigned.StatusCode == HttpStatusCode.OK, "both succeeded");
            var location = (await DepartmentsOfAsync(moved)).Single().Value;
            Assert.True(await ExistsAsync(location), "the employee points at a department that does not exist");
            Assert.Equal(reassigned.StatusCode == HttpStatusCode.OK ? target : source, location);
        }
    }

    // ================= reassign =================

    [Fact]
    public async Task Employees_are_moved_together_and_each_move_is_audited_with_the_old_and_new_department()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var all = await SeedInAsync(company, from, 4, 1000);
        var offboarded = await SeedInAsync(company, from, 1, 1010, EmployeeStatus.Inactive);
        var moving = all.Take(3).Concat(offboarded).ToList();

        var response = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = moving });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(from, body.GetProperty("fromDepartmentId").GetGuid());
        Assert.Equal(to, body.GetProperty("toDepartmentId").GetGuid());
        Assert.Equal(4, body.GetProperty("reassignedCount").GetInt32());
        Assert.Equal(moving.OrderBy(i => i), body.GetProperty("employeeIds").EnumerateArray().Select(e => e.GetGuid()).OrderBy(i => i));
        var now = await DepartmentsOfAsync(all.Concat(offboarded));
        Assert.All(moving, id => Assert.Equal(to, now[id]));
        Assert.Equal(from, now[all[3]]);                                                         // the one not listed stays

        var audits = await AuditAsync(company.CompanyId, "employee.department_changed");
        Assert.Equal(moving.OrderBy(i => i), audits.Select(a => a.TargetId!.Value).OrderBy(i => i));
        Assert.All(audits, a =>
        {
            Assert.Equal("employee", a.Module);
            Assert.Equal(hr.UserId, a.UserId);
            Assert.Equal(company.CompanyId, a.CompanyId);
            Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, a.IpAddress);
            using var old = JsonDocument.Parse(a.OldValue!);
            using var next = JsonDocument.Parse(a.NewValue!);
            Assert.Equal(from, Prop(old.RootElement, "departmentId").GetGuid());
            Assert.Equal(to, Prop(next.RootElement, "departmentId").GetGuid());
            Assert.Single(old.RootElement.EnumerateObject());                                   // nothing but the department id: no name, e-mail or phone
            Assert.Single(next.RootElement.EnumerateObject());
        });

        // The department view and the employee view agree.
        var fromView = await JsonOf(await GetAsync(company, $"/api/departments/{from}", hr.Token));
        var toView = await JsonOf(await GetAsync(company, $"/api/departments/{to}", hr.Token));
        Assert.Equal(1, fromView.GetProperty("employeeCount").GetInt32());
        Assert.Equal(4, toView.GetProperty("employeeCount").GetInt32());
        var employee = await JsonOf(await GetAsync(company, $"/api/employees/{moving[0]}", hr.Token));
        Assert.Equal("To", employee.GetProperty("departmentName").GetString());
    }

    [Fact]
    public async Task Emptying_a_department_by_reassignment_makes_it_deletable()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "Closing");
        var to = await NewDepartmentAsync(company, hr, "Receiving");
        var people = await SeedInAsync(company, from, 3, 1100);

        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(company, hr.Token, from)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = people })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(company, hr.Token, from)).StatusCode);
        Assert.Equal(new[] { to, to, to }, (await DepartmentsOfAsync(people)).Values);
    }

    [Fact]
    public async Task One_bad_employee_rejects_the_whole_request_and_nothing_changes()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var third = await NewDepartmentAsync(company, hr, "Third");
        var good = await SeedInAsync(company, from, 2, 1200);
        var elsewhere = await SeedInAsync(company, third, 1, 1210);
        var foreign = await SeedAsync(other, "Foreign Person", "EMP-0950");
        var auditBefore = (await AuditAsync(company.CompanyId)).Count;

        var missing = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = good.Append(Guid.NewGuid()) });
        var foreignEmployee = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = good.Append(foreign.EmployeeId) });
        var notInSource = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = good.Concat(elsewhere) });

        Assert.All(new[] { missing, foreignEmployee, notInSource }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreignEmployee.Content.ReadAsStringAsync());   // a foreign id looks like a missing one
        Assert.Contains("not in the source department", await notInSource.Content.ReadAsStringAsync());
        Assert.Equal(new[] { from, from }, (await DepartmentsOfAsync(good)).Values);
        Assert.Equal(third, (await DepartmentsOfAsync(elsewhere)).Single().Value);
        Assert.Equal(other.DepartmentId, (await DepartmentsOfAsync(new[] { foreign.EmployeeId })).Single().Value);
        Assert.Equal(auditBefore, (await AuditAsync(company.CompanyId)).Count);                 // no audit entry for a rejected request
        Assert.Empty(await AuditAsync(other.CompanyId, "employee.department_changed"));
    }

    [Fact]
    public async Task Destinations_that_are_foreign_missing_or_the_source_are_refused_and_nothing_moves()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var from = await NewDepartmentAsync(company, hr, "From");
        var foreignDestination = await NewDepartmentAsync(other, otherHr, "Foreign destination");
        var people = await SeedInAsync(company, from, 2, 1300);

        var foreign = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = foreignDestination, employeeIds = people });
        var missing = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = Guid.NewGuid(), employeeIds = people });
        var same = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = from, employeeIds = people });

        Assert.All(new[] { foreign, missing, same }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Foreign", await foreign.Content.ReadAsStringAsync());
        Assert.Contains("different from the source", await same.Content.ReadAsStringAsync());
        Assert.Equal(new[] { from, from }, (await DepartmentsOfAsync(people)).Values);
    }

    [Fact]
    public async Task The_employee_list_must_be_present_non_empty_duplicate_free_and_bounded()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var people = await SeedInAsync(company, from, 2, 1400);

        foreach (var body in new object[]
        {
            new { toDepartmentId = to },                                                          // list omitted
            new { toDepartmentId = to, employeeIds = (Guid[]?)null },
            new { toDepartmentId = to, employeeIds = Array.Empty<Guid>() },
            new { toDepartmentId = to, employeeIds = new[] { people[0], people[0] } },            // duplicate
            new { toDepartmentId = to, employeeIds = new[] { people[0], Guid.Empty } },
            new { toDepartmentId = Guid.Empty, employeeIds = people },
            new { toDepartmentId = to, employeeIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() }   // over the cap
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ReassignAsync(company, hr.Token, from, body)).StatusCode);
        }

        var duplicate = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = new[] { people[0], people[0] } });
        Assert.Contains("duplicates", await duplicate.Content.ReadAsStringAsync());          // the validator's own answer, not a side effect of a later check

        Assert.Equal(new[] { from, from }, (await DepartmentsOfAsync(people)).Values);
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.department_changed"));
    }

    [Fact]
    public async Task An_unknown_or_foreign_source_is_404_and_the_company_in_the_body_is_ignored()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var foreignSource = await NewDepartmentAsync(other, otherHr, "Foreign source");
        var foreignPeople = await SeedInAsync(other, foreignSource, 1, 1500);
        var to = await NewDepartmentAsync(company, hr, "To");

        var unknown = await ReassignAsync(company, hr.Token, Guid.NewGuid(), new { toDepartmentId = to, employeeIds = foreignPeople });
        var foreign = await ReassignAsync(company, hr.Token, foreignSource, new Dictionary<string, object>
        {
            ["toDepartmentId"] = to, ["employeeIds"] = foreignPeople, ["companyId"] = other.CompanyId
        });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Equal(foreignSource, (await DepartmentsOfAsync(foreignPeople)).Single().Value);
    }

    [Fact]
    public async Task Reassigning_needs_authentication_and_the_manage_permission()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var people = await SeedInAsync(company, from, 1, 1600);
        var manager = await ManagerAsync(company);
        var employee = await EmployeeAsync(company);
        var body = new { toDepartmentId = to, employeeIds = people };

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReassignAsync(company, null, from, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReassignAsync(company, manager.Token, from, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReassignAsync(company, employee.Token, from, body)).StatusCode);

        Assert.Equal(from, (await DepartmentsOfAsync(people)).Single().Value);
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.department_changed"));
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_whole_reassignment_back()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var people = await SeedInAsync(company, from, 3, 1700);
        Switch.AuditStageFails = true;

        var response = await ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = people });
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(new[] { from, from, from }, (await DepartmentsOfAsync(people)).Values);
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.department_changed"));
    }

    [Fact]
    public async Task A_concurrent_edit_of_one_employee_fails_the_whole_save_and_no_employee_is_left_half_moved()
    {
        var (company, hr) = await SetupAsync();
        var from = await NewDepartmentAsync(company, hr, "From");
        var to = await NewDepartmentAsync(company, hr, "To");
        var people = await SeedInAsync(company, from, 6, 1800);
        var contested = people.OrderBy(i => i).ElementAt(3);

        // Another administrator has changed one of the employees but not yet committed: the reassignment's UPDATE of that row waits,
        // then finds the version changed (xmin) and the whole save is refused.
        await using var other = Fx.CreateDbContext();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Employees.Where(e => e.Id == contested).ExecuteUpdateAsync(s => s.SetProperty(e => e.Designation, "Edited meanwhile"));

        var reassign = ReassignAsync(company, hr.Token, from, new { toDepartmentId = to, employeeIds = people });
        await Task.Delay(1500);
        Assert.False(reassign.IsCompleted, "the reassignment must wait for the row another administrator is changing");

        await transaction.CommitAsync();

        var response = await reassign;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Enumerable.Repeat(from, 6), (await DepartmentsOfAsync(people)).Values);     // every employee, including the ones updated before the conflict
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.department_changed"));
        await using var db = Fx.CreateDbContext();
        Assert.Equal("Edited meanwhile", (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == contested)).Designation);   // the other edit survived
    }

    [Fact]
    public async Task Two_simultaneous_reassignments_of_the_same_employees_leave_them_all_in_one_place()
    {
        var (company, hr) = await SetupAsync();

        for (var round = 0; round < 4; round++)
        {
            var from = await NewDepartmentAsync(company, hr, $"From {round}");
            var toA = await NewDepartmentAsync(company, hr, $"A {round}");
            var toB = await NewDepartmentAsync(company, hr, $"B {round}");
            var people = await SeedInAsync(company, from, 4, 2000 + round * 10);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(async () => { await gate.Task; return await ReassignAsync(company, hr.Token, from, new { toDepartmentId = toA, employeeIds = people }); });
            var second = Task.Run(async () => { await gate.Task; return await ReassignAsync(company, hr.Token, from, new { toDepartmentId = toB, employeeIds = people }); });
            gate.SetResult();
            var responses = await Task.WhenAll(first, second);

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest));
            var destinations = (await DepartmentsOfAsync(people)).Values.Distinct().ToList();
            Assert.Single(destinations);                                                          // no mixture of A and B
            Assert.Contains(destinations[0], new[] { toA, toB });
        }

        Assert.Equal(4 * 4, (await AuditAsync(company.CompanyId, "employee.department_changed")).Count);   // exactly one entry per move that happened
    }

    [Fact]
    public async Task The_swagger_document_lists_both_operations_with_their_contracts()
    {
        var (company, _) = await SetupAsync();

        using var document = JsonDocument.Parse(await (await GetAsync(company, "/swagger/v1/swagger.json", null)).Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var delete = paths.GetProperty("/api/departments/{id}").GetProperty("delete").GetProperty("responses");
        Assert.True(delete.TryGetProperty("204", out _) && delete.TryGetProperty("404", out _) && delete.TryGetProperty("409", out _));
        var reassign = paths.GetProperty("/api/departments/{id}/reassign-employees").GetProperty("post").GetProperty("responses");
        Assert.True(reassign.TryGetProperty("200", out _) && reassign.TryGetProperty("400", out _) && reassign.TryGetProperty("404", out _) && reassign.TryGetProperty("409", out _));
        Assert.Equal(new[] { "employeeIds", "toDepartmentId" },
            schemas.GetProperty("ReassignEmployeesRequest").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(new[] { "employeeIds", "fromDepartmentId", "reassignedCount", "toDepartmentId" },
            schemas.GetProperty("ReassignEmployeesResponse").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n));
    }
}
