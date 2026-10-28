using System.Net;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>
/// Employee-code allocation under real concurrency: parallel requests through the real API, each with its own database
/// connection and transaction, against the guarded PostgreSQL. The unique index (CompanyId, EmpCode) is the final guard.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmployeeCodeConcurrencyTests : EmployeeApiTestBase
{
    public EmployeeCodeConcurrencyTests(PostgresFixture fx) : base(fx) { }

    private async Task<HttpResponseMessage[]> ParallelCreatesAsync(AuthzCompany company, string token, int count, Func<int, object>? body = null)
    {
        // An asynchronous gate: waiting requests hold no thread-pool thread (a blocking wait would park `count` threads while the
        // in-process server needs the same pool, so under load the requests would start staggered instead of together).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;                                                // release all requests at the same moment
            return await CallAsync(company, HttpMethod.Post, "/api/employees", token,
                body is null ? CreateBody(company.DepartmentId, $"Parallel {i:D2}", $"parallel{i}@example.test") : body(i));
        }).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<List<string>> CodesAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.Employees.AsNoTracking().Where(e => e.CompanyId == companyId && e.EmpCode.StartsWith("EMP-")).Select(e => e.EmpCode).ToListAsync();
    }

    [Fact]
    public async Task Parallel_creations_in_one_company_get_distinct_consecutive_codes()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);

        var responses = await ParallelCreatesAsync(company, hr.Token, 12);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var codes = await CodesAsync(company.CompanyId);
        Assert.Equal(12, codes.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 12).Select(n => $"EMP-{n:D4}").OrderBy(c => c), codes.OrderBy(c => c));    // no duplicates, no gaps
        Assert.Equal(12, (await AuditAsync(company.CompanyId, "employee.created")).Count);                        // one audit row each
    }

    [Fact]
    public async Task Companies_are_numbered_independently_even_when_created_at_the_same_time()
    {
        var companyA = await NewCompanyAsync();
        var companyB = await NewCompanyAsync();
        var hrA = await HrAsync(companyA);
        var hrB = await HrAsync(companyB);

        var all = await Task.WhenAll(ParallelCreatesAsync(companyA, hrA.Token, 6), ParallelCreatesAsync(companyB, hrB.Token, 6));

        Assert.All(all.SelectMany(r => r), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var expected = Enumerable.Range(1, 6).Select(n => $"EMP-{n:D4}").OrderBy(c => c).ToList();
        Assert.Equal(expected, (await CodesAsync(companyA.CompanyId)).OrderBy(c => c));
        Assert.Equal(expected, (await CodesAsync(companyB.CompanyId)).OrderBy(c => c));
    }

    [Fact]
    public async Task Allocation_continues_after_the_highest_numeric_code_and_ignores_other_formats()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedAsync(company, "Imported One", "EMP-0041");
        await SeedAsync(company, "Imported Two", "EMP-0007");
        await SeedAsync(company, "Legacy", "LEGACY-99");
        await SeedAsync(company, "Letters", "EMP-ABC");
        await SeedAsync(company, "Huge", "EMP-12345678901234");               // too many digits to be an int: ignored, not an overflow

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("EMP-0042", (await JsonOf(response)).GetProperty("empCode").GetString());
    }

    [Fact]
    public async Task Codes_beyond_four_digits_keep_counting()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        await SeedAsync(company, "Very Busy Company", "EMP-9999");

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Assert.Equal("EMP-10000", (await JsonOf(response)).GetProperty("empCode").GetString());
    }

    [Fact]
    public async Task Parallel_requests_for_the_same_email_create_exactly_one_and_the_rest_are_409()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var before = await CountsAsync(company.CompanyId);

        var responses = await ParallelCreatesAsync(company, hr.Token, 6, _ => CreateBody(company.DepartmentId, email: "same@example.test"));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));      // never a 500, never a duplicate
        var after = await CountsAsync(company.CompanyId);
        Assert.Equal(before.Users + 1, after.Users);
        Assert.Equal(before.Employees + 1, after.Employees);
        Assert.Equal(before.Audit + 1, after.Audit);                                         // only the winner is audited
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var raw = await conflict.Content.ReadAsStringAsync();
            Assert.DoesNotContain("IX_", raw);
            Assert.DoesNotContain("Npgsql", raw);
        }
    }

    [Fact]
    public async Task Failing_requests_among_parallel_ones_do_not_leave_gaps()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);

        // Every third request names a department that does not exist and fails before a code is allocated.
        var responses = await ParallelCreatesAsync(company, hr.Token, 9, i =>
            CreateBody(i % 3 == 0 ? Guid.NewGuid() : company.DepartmentId, $"Mixed {i}", $"mixed{i}@example.test"));

        // Failure diagnostics only: the exact status and body of every request, so an intermittent failure names its cause.
        var codes = (await CodesAsync(company.CompanyId)).OrderBy(c => c).ToList();
        var diagnostics = new System.Text.StringBuilder();
        for (var i = 0; i < responses.Length; i++)
        {
            diagnostics.AppendLine($"request {i}: {(int)responses[i].StatusCode} {await responses[i].Content.ReadAsStringAsync()}");
        }
        diagnostics.AppendLine("codes: " + string.Join(", ", codes));

        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) == 6, diagnostics.ToString());
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest) == 3, diagnostics.ToString());
        Assert.True(Enumerable.Range(1, 6).Select(n => $"EMP-{n:D4}").OrderBy(c => c).SequenceEqual(codes), diagnostics.ToString());
    }

    [Fact]
    public async Task A_conflicting_insert_that_bypasses_the_generator_is_stopped_by_the_unique_index()
    {
        var company = await NewCompanyAsync();
        await SeedAsync(company, "First", "EMP-0001");

        // The database itself refuses a second EMP-0001, whatever the application layer does.
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SeedAsync(company, "Second", "EMP-0001"));

        Assert.Contains("IX_Employees_CompanyId_EmpCode", duplicate.InnerException!.Message);
        Assert.Single(await CodesAsync(company.CompanyId));
    }

    [Fact]
    public async Task Allocating_a_code_outside_a_transaction_is_refused()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);

        using var scope = Factory.Services.CreateScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(company.CompanyId, company.Domain);
        Assert.Equal(CurrentUserLoadResult.Loaded, await scope.ServiceProvider.GetRequiredService<ICurrentUserLoader>().LoadAsync(hr.UserId, company.CompanyId));

        // The advisory lock would be released immediately without a surrounding transaction, so the generator must refuse.
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IEmployeeCodeGenerator>().NextAsync());
    }
}
