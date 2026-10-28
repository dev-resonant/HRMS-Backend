using HRMS.Application.Features.Roles;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Roles
{
    /// <summary>
    /// Read-side role access. One query, bounded by the authenticated caller's company: a role is visible when it is a built-in role
    /// (no company AND flagged system - the same rule the current-user loader applies) or belongs to the caller's company. A role with no
    /// company that is not flagged system is ignored (fail closed), and nothing is rewritten.
    /// </summary>
    public sealed class RoleQueries : IRoleQueries
    {
        private readonly HrmsDbContext _db;
        private readonly ICurrentUser _currentUser;

        public RoleQueries(HrmsDbContext db, ICurrentUser currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default)
        {
            // Throws when there is no resolved current user, so a missing context can never widen the result.
            var companyId = _currentUser.CompanyId;

            var rows = await _db.Roles.AsNoTracking()
                .Where(r => r.CompanyId == companyId || (r.CompanyId == null && r.IsSystem))
                .Select(r => new { r.Id, r.Name, r.DisplayName, r.IsSystem, IsShared = r.CompanyId == null, r.Permissions })
                .ToListAsync(cancellationToken);

            var order = SystemRoleKeys.All.ToList();

            return rows
                .OrderBy(r => r.IsShared ? 0 : 1)
                .ThenBy(r => r.IsShared ? order.IndexOf(r.Name) is var i && i >= 0 ? i : int.MaxValue : 0)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .Select(r => new RoleDto
                {
                    Id = r.Id,
                    Key = r.Name,
                    DisplayName = r.DisplayName,
                    IsSystem = r.IsSystem,
                    Scope = r.IsShared ? RoleScopes.System : RoleScopes.Company,
                    // The strict parser: anything malformed or outside the catalog is dropped, never granted or reported.
                    Permissions = RolePermissions.Parse(r.Permissions)
                })
                .ToList();
        }
    }
}
