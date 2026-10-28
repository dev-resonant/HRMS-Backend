using System.Net;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Features.Employees.Commands.DeactivateEmployee;
using HRMS.Application.Features.Employees.Commands.UpdateEmployee;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Enums;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>
/// Two administrators changing the same employee at the same time. Each test interleaves two request scopes (two database
/// connections) deterministically: scope A loads the employee, scope B commits a change, then scope A saves.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmployeeConcurrencyTests : EmployeeApiTestBase
{
    public EmployeeConcurrencyTests(PostgresFixture fx) : base(fx) { }

    private sealed record Setup(AuthzCompany Company, AuthzUser Hr, SeededEmployee Target);

    private async Task<Setup> SetupAsync()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        var target = await SeedAsync(company, "Tara Target", "EMP-0010", designation: "Engineer", joiningDate: new DateOnly(2024, 1, 15));
        return new Setup(company, hr, target);
    }

    /// <summary>A request scope as the authorization filter would leave it: tenant resolved, current user loaded.</summary>
    private async Task<IServiceScope> ScopeAsync(Setup s)
    {
        var scope = Factory.Services.CreateScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(s.Company.CompanyId, s.Company.Domain);
        Assert.Equal(CurrentUserLoadResult.Loaded, await scope.ServiceProvider.GetRequiredService<ICurrentUserLoader>().LoadAsync(s.Hr.UserId, s.Company.CompanyId));
        return scope;
    }

    private static UpdateEmployeeCommand UpdateTo(Setup s, string designation, string status = "active")
        => new(s.Target.EmployeeId, "Tara Target", s.Company.DepartmentId, null, designation, new DateOnly(2024, 1, 15), status, null);

    [Fact]
    public async Task A_stale_edit_cannot_silently_overwrite_a_change_committed_in_between()
    {
        var s = await SetupAsync();
        using var scopeA = await ScopeAsync(s);
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IEmployeeRepository>();

        var loadedByA = await repositoryA.GetForUpdateAsync(s.Target.EmployeeId);          // A reads "Engineer"
        using (var scopeB = await ScopeAsync(s))
        {
            await scopeB.ServiceProvider.GetRequiredService<IMediator>().Send(UpdateTo(s, "Written by B"));   // B commits first
        }

        loadedByA!.Designation = "Written by A";
        var failure = await Assert.ThrowsAsync<ConflictException>(() => repositoryA.SaveChangesAsync());

        Assert.Contains("changed by someone else", failure.Message);
        Assert.Equal("Written by B", (await LoadAsync(s.Target.EmployeeId)).Employee.Designation);       // B's change survived
    }

    [Fact]
    public async Task An_edit_cannot_resurrect_an_employee_who_was_deactivated_in_between()
    {
        var s = await SetupAsync();
        using var scopeA = await ScopeAsync(s);
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IEmployeeRepository>();

        var loadedByA = await repositoryA.GetForUpdateAsync(s.Target.EmployeeId);          // A reads an active employee
        using (var scopeB = await ScopeAsync(s))
        {
            await scopeB.ServiceProvider.GetRequiredService<IMediator>().Send(new DeactivateEmployeeCommand(s.Target.EmployeeId, new DateOnly(2024, 12, 31)));
        }

        loadedByA!.Status = EmployeeStatus.Probation;                                      // A's stale form says "probation"
        await Assert.ThrowsAsync<ConflictException>(() => repositoryA.SaveChangesAsync());

        var (user, employee) = await LoadAsync(s.Target.EmployeeId);
        Assert.Equal(EmployeeStatus.Inactive, employee.Status);                            // still deactivated...
        Assert.False(user.IsActive);                                                        // ...consistently
    }

    [Fact]
    public async Task Two_deactivations_that_overlap_produce_one_success_one_conflict_and_one_audit_row()
    {
        var s = await SetupAsync();
        using var scopeA = await ScopeAsync(s);
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IEmployeeRepository>();

        var loadedByA = await repositoryA.GetForUpdateAsync(s.Target.EmployeeId);
        using (var scopeB = await ScopeAsync(s))
        {
            await scopeB.ServiceProvider.GetRequiredService<IMediator>().Send(new DeactivateEmployeeCommand(s.Target.EmployeeId, new DateOnly(2024, 12, 31)));
        }

        loadedByA!.Status = EmployeeStatus.Inactive;
        loadedByA.LastWorkingDate = new DateOnly(2025, 1, 31);
        await Assert.ThrowsAsync<ConflictException>(() => repositoryA.SaveChangesAsync());

        Assert.Equal(new DateOnly(2024, 12, 31), (await LoadAsync(s.Target.EmployeeId)).Employee.LastWorkingDate);   // the first one stands
        Assert.Single(await AuditAsync(s.Company.CompanyId, "employee.deactivated"));
    }

    [Fact]
    public async Task Through_the_api_a_conflicting_save_is_a_409_with_a_generic_message_and_leaves_the_winning_change()
    {
        var s = await SetupAsync();
        using var scopeA = await ScopeAsync(s);

        // The first update commits normally through HTTP; a second save that started from the earlier state is refused.
        var first = await CallAsync(s.Company, HttpMethod.Put, $"/api/employees/{s.Target.EmployeeId}", s.Hr.Token,
            new { name = "Tara Target", departmentId = s.Company.DepartmentId, managerId = (Guid?)null, designation = "First", joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var repositoryA = scopeA.ServiceProvider.GetRequiredService<IEmployeeRepository>();
        var stale = await repositoryA.GetForUpdateAsync(s.Target.EmployeeId);               // A starts after "First"
        var second = await CallAsync(s.Company, HttpMethod.Put, $"/api/employees/{s.Target.EmployeeId}", s.Hr.Token,
            new { name = "Tara Target", departmentId = s.Company.DepartmentId, managerId = (Guid?)null, designation = "Second", joiningDate = "2024-01-15", status = "active", lastWorkingDate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        stale!.Designation = "Stale";
        await Assert.ThrowsAsync<ConflictException>(() => repositoryA.SaveChangesAsync());
        Assert.Equal("Second", (await LoadAsync(s.Target.EmployeeId)).Employee.Designation);
    }
}
