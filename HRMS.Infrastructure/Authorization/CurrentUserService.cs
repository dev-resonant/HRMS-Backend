using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Authorization
{
    /// <summary>
    /// Request-scoped current user. <see cref="LoadAsync"/> reads the user, role (with its permissions JSON) and employee id
    /// from the database on every protected request, so a role-permission change applies to the very next request even with
    /// the same JWT (decision D5). Nothing but the user id and company id is trusted from the token.
    /// </summary>
    public sealed class CurrentUserService : ICurrentUser, ICurrentUserLoader
    {
        private readonly HrmsDbContext _dbContext;
        private readonly ITenantContext _tenantContext;
        private Snapshot? _snapshot;

        public CurrentUserService(HrmsDbContext dbContext, ITenantContext tenantContext)
        {
            _dbContext = dbContext;
            _tenantContext = tenantContext;
        }

        public bool IsResolved => _snapshot is not null;

        public Guid UserId => Required.UserId;

        public Guid CompanyId => Required.CompanyId;

        public string RoleKey => Required.RoleKey;

        public string RoleDisplayName => Required.RoleDisplayName;

        public Guid? EmployeeId => Required.EmployeeId;

        public bool PasswordChangeRequired => Required.PasswordChangeRequired;

        public bool IsPlatform => Required.IsPlatform;

        public IReadOnlyDictionary<string, string[]> Permissions => Required.Permissions;

        public bool HasPermission(string module, string action)
        {
            // Unknown modules/actions never appear in Permissions (the parser drops them), so they always deny.
            return PermissionCatalog.IsValid(module, action)
                && Required.Permissions.TryGetValue(module, out var actions)
                && actions.Contains(action);
        }

        public async Task<CurrentUserLoadResult> LoadAsync(Guid userId, Guid tokenCompanyId, CancellationToken cancellationToken = default)
        {
            _snapshot = null;

            if (userId == Guid.Empty
                || !_tenantContext.IsResolved
                || _tenantContext.CompanyId == Guid.Empty
                || tokenCompanyId != _tenantContext.CompanyId)
            {
                return CurrentUserLoadResult.NotFound;
            }

            var companyId = _tenantContext.CompanyId;

            var row = await _dbContext.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.CompanyId == companyId)
                .Select(u => new
                {
                    u.IsActive,
                    u.IsFirstLogin,
                    RoleCompanyId = u.Role.CompanyId,
                    RoleIsSystem = u.Role.IsSystem,
                    RoleName = u.Role.Name,
                    RoleDisplayName = u.Role.DisplayName,
                    u.Role.Permissions,
                    EmployeeId = u.Employee == null ? (Guid?)null : u.Employee.Id,
                    EmployeeCompanyId = u.Employee == null ? (Guid?)null : u.Employee.CompanyId,

                    // Same round trip: is the caller's company the platform company, and is it the ONLY one designated?
                    CompanyIsPlatform = u.Company.IsPlatform,
                    DesignatedPlatformCompanies = _dbContext.Companies.Count(c => c.IsPlatform)
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return CurrentUserLoadResult.NotFound;
            }

            if (!row.IsActive)
            {
                return CurrentUserLoadResult.Inactive;
            }

            // The database has no cross-company consistency constraints, so verify them here and fail closed:
            // a role must be a system role (no company) or belong to the user's company; an employee record must too.
            var roleBelongsHere = row.RoleCompanyId == companyId || (row.RoleCompanyId is null && row.RoleIsSystem);

            if (!roleBelongsHere || (row.EmployeeCompanyId is not null && row.EmployeeCompanyId != companyId))
            {
                return CurrentUserLoadResult.Denied;
            }

            _snapshot = new Snapshot(
                userId,
                companyId,
                row.RoleName,
                row.RoleDisplayName,
                row.EmployeeId,
                row.IsFirstLogin,
                RolePermissions.Parse(row.Permissions),
                // Fail closed: not designated, or more than one company designated (the unique index should make that impossible) = not platform.
                row.CompanyIsPlatform && row.DesignatedPlatformCompanies == 1);

            return CurrentUserLoadResult.Loaded;
        }

        private Snapshot Required => _snapshot
            ?? throw new UnauthorizedAccessException("The current user could not be determined.");

        private sealed record Snapshot(
            Guid UserId,
            Guid CompanyId,
            string RoleKey,
            string RoleDisplayName,
            Guid? EmployeeId,
            bool PasswordChangeRequired,
            IReadOnlyDictionary<string, string[]> Permissions,
            bool IsPlatform);
    }
}
