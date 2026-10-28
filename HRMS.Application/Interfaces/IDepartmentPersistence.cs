using HRMS.Application.Features.Departments;
using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    /// <summary>Write-side access to departments. Every member is bound to the authenticated caller's company.</summary>
    public interface IDepartmentRepository
    {
        /// <summary>Tracked department, or null when it does not exist in the caller's company.</summary>
        Task<Department?> GetForUpdateAsync(Guid departmentId, CancellationToken cancellationToken = default);

        void Add(Department department);

        /// <summary>Marks the department for deletion by the next save.</summary>
        void Remove(Department department);

        /// <summary>Employee records (any status) assigned to the department of the caller's company.</summary>
        Task<int> CountEmployeesAsync(Guid departmentId, CancellationToken cancellationToken = default);

        /// <summary>Departments of the caller's company nested directly under the department.</summary>
        Task<int> CountChildDepartmentsAsync(Guid departmentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Serializes hierarchy changes of the caller's company until the request's transaction ends, so two concurrent re-parentings
        /// cannot each pass the cycle check and together create a cycle. Must be called inside the request's transaction.
        /// </summary>
        Task LockHierarchyAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// True when making <paramref name="newParentId"/> the parent of <paramref name="departmentId"/> would create a cycle (the new
        /// parent is the department itself or one of its descendants). A chain too long to walk is treated as a cycle (fail closed).
        /// </summary>
        Task<bool> WouldCreateCycleAsync(Guid departmentId, Guid newParentId, CancellationToken cancellationToken = default);

        /// <summary>Persists the pending changes; a database conflict becomes a <c>ConflictException</c> with a generic message.</summary>
        Task SaveChangesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Read-side access to departments as DTOs.</summary>
    public interface IDepartmentQueries
    {
        /// <summary>One department within the caller's read scope (all of the company, or only their own department), or null.</summary>
        Task<DepartmentDto?> GetAsync(Guid departmentId, CancellationToken cancellationToken = default);

        /// <summary>The department within the caller's COMPANY, ignoring the read scope. Only for answering a write the caller was already authorized to make.</summary>
        Task<DepartmentDto?> GetInCompanyAsync(Guid departmentId, CancellationToken cancellationToken = default);

        /// <summary>Every department within the caller's read scope, ordered by name.</summary>
        Task<IReadOnlyList<DepartmentDto>> ListAsync(CancellationToken cancellationToken = default);
    }
}
