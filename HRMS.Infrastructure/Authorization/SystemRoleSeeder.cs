using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Authorization
{
    /// <summary>
    /// Makes sure the four system roles exist (CompanyId NULL, IsSystem). Idempotent and insert-only: an existing system role
    /// is never rewritten, so permission changes made later are preserved. The AddSystemRoles migration seeds the same rows.
    /// </summary>
    public static class SystemRoleSeeder
    {
        public static async Task<Role> EnsureAsync(HrmsDbContext dbContext, string requiredKey, CancellationToken cancellationToken = default)
        {
            var existing = await dbContext.Roles
                .Where(r => r.CompanyId == null && SystemRoleKeys.All.Contains(r.Name))
                .Select(r => r.Name)
                .ToListAsync(cancellationToken);

            var added = false;

            foreach (var definition in SystemRoles.Definitions.Where(d => !existing.Contains(d.Key)))
            {
                dbContext.Roles.Add(new Role
                {
                    Id = definition.Id,
                    CompanyId = null,
                    Name = definition.Key,
                    DisplayName = definition.DisplayName,
                    Permissions = definition.PermissionsJson,
                    IsSystem = true
                });
                added = true;
            }

            if (added)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return await dbContext.Roles.SingleAsync(r => r.CompanyId == null && r.Name == requiredKey, cancellationToken);
        }
    }
}
