using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Write-side access to roles (foundation for a future permission-update handler; no endpoint uses it yet). Everything is bound to the
    /// authenticated caller's company, with the same visibility as the read side: the built-in roles and the caller's own company's roles.
    /// </summary>
    public interface IRoleRepository
    {
        /// <summary>The tracked role (with its concurrency version as read), or null when it is not visible to the caller's company.</summary>
        Task<Role?> GetForUpdateAsync(Guid roleId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the pending role changes. If the role was changed by another transaction since it was read, nothing is written and a
        /// <see cref="Common.Exceptions.ConcurrencyConflictException"/> is thrown. Every other failure (a constraint, an invalid value, a lost
        /// connection) is NOT translated and surfaces unchanged, so it cannot be mistaken for a stale update.
        /// </summary>
        Task SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
