using HRMS.Application.Features.Employees;
using HRMS.Application.Features.Employees.Queries.ListEmployees;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;

namespace HRMS.Application.Interfaces
{
    /// <summary>Write-side access to employees. Every member is bound to the authenticated caller's company.</summary>
    public interface IEmployeeRepository
    {
        /// <summary>Tracked employee with its user, or null when it does not exist in the caller's company.</summary>
        Task<Employee?> GetForUpdateAsync(Guid employeeId, CancellationToken cancellationToken = default);

        /// <summary>Tracked employees of the caller's company with these ids (missing and foreign ids are simply absent from the result).</summary>
        Task<List<Employee>> GetManyForUpdateAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

        /// <summary>Status of an employee of the caller's company, or null when there is none.</summary>
        Task<EmployeeStatus?> GetStatusAsync(Guid employeeId, CancellationToken cancellationToken = default);

        /// <summary>True when a user with this (normalized) email already exists in the caller's company.</summary>
        Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default);

        /// <summary>Id of the built-in role with this key (employee, manager, ...).</summary>
        Task<Guid> GetSystemRoleIdAsync(string roleKey, CancellationToken cancellationToken = default);

        void Add(User user, Employee employee);

        /// <summary>Forces the employee row to be written by the next save even if none of its own columns changed, so its version advances.</summary>
        void MarkChanged(Employee employee);

        /// <summary>
        /// True when making <paramref name="newManagerId"/> the manager of <paramref name="employeeId"/> would create a reporting
        /// cycle (the new manager is the employee, or reports to the employee directly or indirectly).
        /// </summary>
        Task<bool> WouldCreateReportingCycleAsync(Guid employeeId, Guid newManagerId, CancellationToken cancellationToken = default);

        /// <summary>Employees of the caller's company that are not inactive and report directly to this one.</summary>
        Task<int> CountActiveDirectReportsAsync(Guid managerId, CancellationToken cancellationToken = default);

        /// <summary>Deletes every session of the user (all devices); returns how many were deleted. Joins the open transaction.</summary>
        Task<int> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the pending changes. Database conflicts (duplicate email or employee code, a referenced row that no longer
        /// exists) are translated to <c>ConflictException</c> with a generic message; no database detail leaves this method.
        /// </summary>
        Task SaveChangesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Read-side access to employees as DTOs.</summary>
    public interface IEmployeeQueries
    {
        /// <summary>One employee within the caller's data scope (own / team / company), or null if missing, foreign or out of scope.</summary>
        Task<EmployeeDto?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default);

        /// <summary>The employee within the caller's COMPANY, ignoring the read scope. Only for answering a write the caller was already authorized to make.</summary>
        Task<EmployeeDto?> GetInCompanyAsync(Guid employeeId, CancellationToken cancellationToken = default);

        /// <summary>A page of employees within the caller's data scope. Criteria must already be validated.</summary>
        Task<PagedResult<EmployeeDto>> ListAsync(ListEmployeesQuery criteria, CancellationToken cancellationToken = default);
    }

    /// <summary>Allocates the next employee code (EMP-0001, EMP-0002, ...) for the caller's company.</summary>
    public interface IEmployeeCodeGenerator
    {
        /// <summary>
        /// Must be called inside the request's transaction. Concurrent callers of the same company are serialized until their
        /// transaction ends, so two of them can never receive the same code; the unique index remains the final guard.
        /// </summary>
        Task<string> NextAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Produces the random one-time password given to a new account. The plaintext must never be stored, logged or audited.</summary>
    public interface ITemporaryPasswordGenerator
    {
        string Generate();
    }
}
