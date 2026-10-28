using HRMS.Application.Features.Roles;

namespace HRMS.Application.Interfaces
{
    /// <summary>Read-side access to roles as DTOs.</summary>
    public interface IRoleQueries
    {
        /// <summary>
        /// The roles visible to the caller: the built-in (system) roles, which every company shares, and the custom roles of the
        /// caller's own company. Another company's roles are never returned. Built-in roles come first in hierarchy order
        /// (employee, manager, hr_admin, super_admin), then company roles by name.
        /// </summary>
        Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default);
    }
}
