using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Roles
{
    /// <summary>Write-side role access. Bound to the caller's company (ICurrentUser fails closed when unresolved).</summary>
    public sealed class RoleRepository : IRoleRepository
    {
        private readonly HrmsDbContext _db;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<RoleRepository> _logger;

        public RoleRepository(HrmsDbContext db, ICurrentUser currentUser, ILogger<RoleRepository> logger)
        {
            _db = db;
            _currentUser = currentUser;
            _logger = logger;
        }

        public Task<Role?> GetForUpdateAsync(Guid roleId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            // The same visibility rule as RoleQueries: a built-in role (no company AND flagged system) or a role of the caller's company.
            return _db.Roles.FirstOrDefaultAsync(
                r => r.Id == roleId && (r.CompanyId == companyId || (r.CompanyId == null && r.IsSystem)), cancellationToken);
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Only the concurrency exception is translated: the UPDATE matched no row because the role's xmin changed (or the row is gone).
                _logger.LogWarning("A role change was refused because the role was modified concurrently.");

                throw new ConcurrencyConflictException("The role was changed by someone else. Reload the role and try again.");
            }
        }
    }
}
