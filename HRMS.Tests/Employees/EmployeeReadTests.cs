using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Employees;

/// <summary>GET /api/employees/{id} and GET /api/employees against the real API and PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeReadTests : EmployeeApiTestBase
{
    public EmployeeReadTests(PostgresFixture fx) : base(fx) { }

    // A small company: manager M with reports R1 and R2, an unrelated peer P, and HR.
    private sealed record Org(AuthzCompany Company, AuthzUser Hr, SeededEmployee Manager, SeededEmployee R1, SeededEmployee R2, SeededEmployee Peer, SeededEmployee ReportOfReport);

    private async Task<Org> OrgAsync()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var manager = await SeedAsync(company, "Mira Manager", "EMP-0001", SystemRoleKeys.Manager);
        var r1 = await SeedAsync(company, "Ravi Report", "EMP-0002", managerEmployeeId: manager.EmployeeId);
        var r2 = await SeedAsync(company, "Rita Report", "EMP-0003", managerEmployeeId: manager.EmployeeId);
        var peer = await SeedAsync(company, "Paul Peer", "EMP-0004");
        var deep = await SeedAsync(company, "Dana Deep", "EMP-0005", managerEmployeeId: r1.EmployeeId);
        return new Org(company, hr, manager, r1, r2, peer, deep);
    }

    private async Task<List<Guid>> ListIdsAsync(AuthzCompany company, string token, string query = "")
    {
        var response = await GetAsync(company, "/api/employees" + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    // ================= GET /api/employees/{id} =================

    [Fact]
    public async Task HR_can_get_any_employee_of_the_company_with_the_full_contract()
    {
        var org = await OrgAsync();

        var response = await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", org.Hr.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(org.R1.EmployeeId, body.GetProperty("id").GetGuid());
        Assert.Equal("EMP-0002", body.GetProperty("empCode").GetString());
        Assert.Equal("Ravi Report", body.GetProperty("name").GetString());
        Assert.Equal(org.R1.Email, body.GetProperty("email").GetString());
        Assert.Equal(org.Manager.EmployeeId, body.GetProperty("managerId").GetGuid());
        Assert.Equal("Mira Manager", body.GetProperty("managerName").GetString());
        Assert.Equal("Engineering", body.GetProperty("departmentName").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal("employee", body.GetProperty("role").GetString());
        Assert.DoesNotContain(body.EnumerateObject().Select(p => p.Name.ToLowerInvariant()), n => n.Contains("company") || n.Contains("password") || n.Contains("hash"));
    }

    [Fact]
    public async Task A_manager_can_get_themself_and_direct_reports_but_nobody_else()
    {
        var org = await OrgAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(org.Company, $"/api/employees/{org.Manager.EmployeeId}", org.Manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", org.Manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(org.Company, $"/api/employees/{org.R2.EmployeeId}", org.Manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, $"/api/employees/{org.Peer.EmployeeId}", org.Manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, $"/api/employees/{org.ReportOfReport.EmployeeId}", org.Manager.Token)).StatusCode);   // only DIRECT reports
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, $"/api/employees/{org.Hr.EmployeeId}", org.Manager.Token)).StatusCode);
    }

    [Fact]
    public async Task An_employee_can_get_only_their_own_record()
    {
        var org = await OrgAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(org.Company, $"/api/employees/{org.Peer.EmployeeId}", org.Peer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", org.Peer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, $"/api/employees/{org.Manager.EmployeeId}", org.R1.Token)).StatusCode);   // not even their own manager
    }

    [Fact]
    public async Task Missing_foreign_and_out_of_scope_employees_all_answer_identically()
    {
        var org = await OrgAsync();
        var other = await NewCompanyAsync();
        var foreign = await SeedAsync(other, "Fiona Foreign", "EMP-0001");

        var missing = await GetAsync(org.Company, $"/api/employees/{Guid.NewGuid()}", org.Hr.Token);
        var foreignForHr = await GetAsync(org.Company, $"/api/employees/{foreign.EmployeeId}", org.Hr.Token);
        var outOfScope = await GetAsync(org.Company, $"/api/employees/{org.Peer.EmployeeId}", org.Manager.Token);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignForHr.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        var expected = await missing.Content.ReadAsStringAsync();
        Assert.Equal(expected, await foreignForHr.Content.ReadAsStringAsync());      // no way to learn that a foreign id exists
        Assert.Equal(expected, await outOfScope.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_token_of_another_company_cannot_read_here_and_unauthenticated_calls_are_401()
    {
        var org = await OrgAsync();
        var other = await NewCompanyAsync();
        var foreignHr = await HrAsync(other);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", foreignHr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(org.Company, "/api/employees/not-a-guid", org.Hr.Token)).StatusCode);
    }

    [Fact]
    public async Task A_role_without_any_employee_view_permission_gets_403()
    {
        var org = await OrgAsync();
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(Fx, org.Company.CompanyId, "{}");
        var nobody = await AuthzTestData.AddUserAsync(Fx, Factory, org.Company, roleId, "Custom");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", nobody.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(org.Company, "/api/employees", nobody.Token)).StatusCode);
    }

    // ================= GET /api/employees =================

    [Fact]
    public async Task An_employee_gets_403_from_the_list_and_unauthenticated_gets_401()
    {
        var org = await OrgAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(org.Company, "/api/employees", org.Peer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(org.Company, "/api/employees", null)).StatusCode);
    }

    [Fact]
    public async Task A_manager_lists_themself_and_direct_reports_only()
    {
        var org = await OrgAsync();

        var ids = await ListIdsAsync(org.Company, org.Manager.Token);

        Assert.Equal(new[] { org.Manager.EmployeeId, org.R1.EmployeeId, org.R2.EmployeeId }.OrderBy(x => x), ids.OrderBy(x => x));
    }

    [Fact]
    public async Task HR_lists_the_whole_company_and_nothing_from_another_company()
    {
        var org = await OrgAsync();
        var other = await NewCompanyAsync();
        var foreign = await SeedAsync(other, "Fiona Foreign", "EMP-0001");

        var ids = await ListIdsAsync(org.Company, org.Hr.Token);

        Assert.Equal(
            new[] { org.Hr.EmployeeId!.Value, org.Manager.EmployeeId, org.R1.EmployeeId, org.R2.EmployeeId, org.Peer.EmployeeId, org.ReportOfReport.EmployeeId }.OrderBy(x => x),
            ids.OrderBy(x => x));
        Assert.DoesNotContain(foreign.EmployeeId, ids);
    }

    [Fact]
    public async Task The_list_item_contract_matches_the_single_employee_contract()
    {
        var org = await OrgAsync();

        var list = await JsonOf(await GetAsync(org.Company, "/api/employees?search=Ravi", org.Hr.Token));
        var single = await JsonOf(await GetAsync(org.Company, $"/api/employees/{org.R1.EmployeeId}", org.Hr.Token));

        Assert.Equal(single.GetRawText(), list.GetProperty("items")[0].GetRawText());
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    // ---------- pagination ----------

    [Fact]
    public async Task Pagination_returns_consistent_non_overlapping_pages()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var seeded = await SeedManyAsync(company, 25);

        var page1 = await JsonOf(await GetAsync(company, "/api/employees?pageSize=10&page=1&sortBy=empCode", hr.Token));
        var page2 = await JsonOf(await GetAsync(company, "/api/employees?pageSize=10&page=2&sortBy=empCode", hr.Token));
        var page3 = await JsonOf(await GetAsync(company, "/api/employees?pageSize=10&page=3&sortBy=empCode", hr.Token));
        var page4 = await JsonOf(await GetAsync(company, "/api/employees?pageSize=10&page=4&sortBy=empCode", hr.Token));

        Assert.Equal(26, page1.GetProperty("totalCount").GetInt32());            // 25 + the HR user's own record
        Assert.Equal(3, page1.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, page1.GetProperty("page").GetInt32());
        Assert.Equal(10, page1.GetProperty("pageSize").GetInt32());
        Assert.Equal(10, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(10, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(6, page3.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page4.GetProperty("items").GetArrayLength());            // beyond the last page: empty, not an error
        Assert.Equal(26, page4.GetProperty("totalCount").GetInt32());

        var all = new[] { page1, page2, page3 }.SelectMany(p => p.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())).ToList();
        Assert.Equal(26, all.Distinct().Count());                                // every employee exactly once
        Assert.Equal(seeded.OrderBy(x => x), all.Where(seeded.Contains).OrderBy(x => x));
    }

    [Fact]
    public async Task The_default_page_size_is_20_and_the_maximum_100_is_accepted()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedManyAsync(company, 120);

        var defaults = await JsonOf(await GetAsync(company, "/api/employees", hr.Token));
        var max = await JsonOf(await GetAsync(company, "/api/employees?pageSize=100", hr.Token));

        Assert.Equal(20, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(20, defaults.GetProperty("items").GetArrayLength());
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(100, max.GetProperty("items").GetArrayLength());
        Assert.Equal(121, max.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, max.GetProperty("totalPages").GetInt32());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=-5")]
    [InlineData("page=abc")]
    [InlineData("pageSize=1.5")]
    [InlineData("sortBy=password")]
    [InlineData("sortBy=name%3B%20DROP%20TABLE%20%22Employees%22")]
    [InlineData("sortBy=user.passwordHash")]
    [InlineData("sortDirection=sideways")]
    [InlineData("status=retired")]
    [InlineData("departmentId=not-a-guid")]
    public async Task Invalid_paging_sorting_and_filters_are_rejected_with_400(string query)
    {
        var org = await OrgAsync();

        var response = await GetAsync(org.Company, "/api/employees?" + query, org.Hr.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_search_text_longer_than_100_characters_is_rejected()
    {
        var org = await OrgAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(org.Company, "/api/employees?search=" + new string('a', 101), org.Hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(org.Company, "/api/employees?search=" + new string('a', 100), org.Hr.Token)).StatusCode);
    }

    // ---------- filters ----------

    [Fact]
    public async Task Filtering_by_status_and_department_works_and_never_crosses_companies()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var finance = await AddDepartmentAsync(company.CompanyId, "Finance");
        var probation = await SeedManyAsync(company, 2, 1, "Prob", EmployeeStatus.Probation);
        var inFinance = await SeedManyAsync(company, 3, 10, "Fin", departmentId: finance);
        var foreign = await NewCompanyAsync();
        var foreignIds = await SeedManyAsync(foreign, 2, 1, "Prob", EmployeeStatus.Probation);

        var byStatus = await ListIdsAsync(company, hr.Token, "?status=probation");
        var byStatusMixedCase = await ListIdsAsync(company, hr.Token, "?status=PROBATION");
        var byDepartment = await ListIdsAsync(company, hr.Token, $"?departmentId={finance}");
        var foreignDepartment = await ListIdsAsync(company, hr.Token, $"?departmentId={foreign.DepartmentId}");
        var unknownDepartment = await ListIdsAsync(company, hr.Token, $"?departmentId={Guid.NewGuid()}");

        Assert.Equal(probation.OrderBy(x => x), byStatus.OrderBy(x => x));
        Assert.Equal(byStatus.OrderBy(x => x), byStatusMixedCase.OrderBy(x => x));
        Assert.Equal(inFinance.OrderBy(x => x), byDepartment.OrderBy(x => x));
        Assert.Empty(foreignDepartment);                                  // a foreign department filter reveals nothing
        Assert.Empty(unknownDepartment);
        Assert.Empty(byStatus.Intersect(foreignIds));
    }

    // ---------- sorting ----------

    [Fact]
    public async Task Sorting_by_name_in_both_directions()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedAsync(company, "Charlie Three", "EMP-0003");
        await SeedAsync(company, "Alpha One", "EMP-0001");
        await SeedAsync(company, "Bravo Two", "EMP-0002");

        async Task<List<string>> NamesAsync(string query)
        {
            var body = await JsonOf(await GetAsync(company, "/api/employees?" + query, hr.Token));
            return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).Where(n => n.Contains(' ') && !n.StartsWith("User")).ToList();
        }

        Assert.Equal(new[] { "Alpha One", "Bravo Two", "Charlie Three" }, await NamesAsync("sortBy=name&sortDirection=asc"));
        Assert.Equal(new[] { "Charlie Three", "Bravo Two", "Alpha One" }, await NamesAsync("sortBy=NAME&sortDirection=DESC"));
    }

    [Fact]
    public async Task Sorting_by_employee_code_is_numeric_even_beyond_four_digits()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedAsync(company, "Ten Thousand", "EMP-10000");
        await SeedAsync(company, "Nine Nine", "EMP-9999");
        await SeedAsync(company, "Two", "EMP-0002");

        var asc = await JsonOf(await GetAsync(company, "/api/employees?sortBy=empCode", hr.Token));
        var desc = await JsonOf(await GetAsync(company, "/api/employees?sortBy=empCode&sortDirection=desc", hr.Token));

        string[] Codes(JsonElement body) => body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("empCode").GetString()!).Where(c => c.StartsWith("EMP-")).ToArray();

        Assert.Equal(new[] { "EMP-0002", "EMP-9999", "EMP-10000" }, Codes(asc));
        Assert.Equal(new[] { "EMP-10000", "EMP-9999", "EMP-0002" }, Codes(desc));
    }

    [Fact]
    public async Task Sorting_by_joining_date_and_the_default_order_are_stable()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var late = await SeedAsync(company, "Late Joiner", "EMP-0001", joiningDate: new DateOnly(2025, 6, 1));
        var early = await SeedAsync(company, "Early Joiner", "EMP-0002", joiningDate: new DateOnly(2020, 1, 1));

        var byDate = await ListIdsAsync(company, hr.Token, "?sortBy=joiningDate&search=Joiner");
        var byDateDesc = await ListIdsAsync(company, hr.Token, "?sortBy=joiningDate&sortDirection=desc&search=Joiner");
        var defaultOrder = await ListIdsAsync(company, hr.Token, "?search=Joiner");

        Assert.Equal(new[] { early.EmployeeId, late.EmployeeId }, byDate);
        Assert.Equal(new[] { late.EmployeeId, early.EmployeeId }, byDateDesc);
        Assert.Equal(new[] { late.EmployeeId, early.EmployeeId }, defaultOrder);      // default: by employee code ascending
    }

    // ---------- search ----------

    [Fact]
    public async Task Search_matches_name_code_designation_and_email_case_insensitively()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var target = await SeedAsync(company, "Zelda Quixote", "EMP-0042", designation: "Principal Wizard", email: "zq.unique@example.test");
        await SeedAsync(company, "Someone Else", "EMP-0043");

        foreach (var term in new[] { "zelda", "QUIXOTE", "emp-0042", "principal wiz", "zq.unique" })
        {
            var ids = await ListIdsAsync(company, hr.Token, "?search=" + Uri.EscapeDataString(term));
            Assert.Equal(new[] { target.EmployeeId }, ids);
        }
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("a%z")]
    [InlineData("\\")]
    public async Task Wildcard_characters_in_the_search_are_literal(string term)
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedManyAsync(company, 5);

        var ids = await ListIdsAsync(company, hr.Token, "?search=" + Uri.EscapeDataString(term));

        Assert.Empty(ids);                                                // nothing contains these literal characters
    }

    [Fact]
    public async Task A_managers_search_never_leaks_employees_outside_the_team()
    {
        var org = await OrgAsync();

        Assert.Empty(await ListIdsAsync(org.Company, org.Manager.Token, "?search=Paul"));              // the peer
        Assert.Empty(await ListIdsAsync(org.Company, org.Manager.Token, "?search=Dana"));              // report of a report
        Assert.Equal(new[] { org.R1.EmployeeId }, await ListIdsAsync(org.Company, org.Manager.Token, "?search=Ravi"));
    }
}
