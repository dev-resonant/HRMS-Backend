using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Common
{
    /// <summary>Account and setup-token access bound to the tenant resolved from the request host (works before anyone is authenticated).</summary>
    public sealed class AccountRepository : IAccountRepository
    {
        private readonly HrmsDbContext _db;
        private readonly ITenantContext _tenant;

        public AccountRepository(HrmsDbContext db, ITenantContext tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        private Guid CompanyId => _tenant.IsResolved
            ? _tenant.CompanyId
            : throw new InvalidOperationException("The tenant has not been resolved.");

        public Task<User?> GetUserForUpdateAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var companyId = CompanyId;

            return _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.CompanyId == companyId, cancellationToken);
        }

        public void AddToken(AccountSetupToken token) => _db.AccountSetupTokens.Add(token);

        public void AddOutboxMessage(OutboxMessage message) => _db.OutboxMessages.Add(message);

        public Task<int> RevokeOpenTokensAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var companyId = CompanyId;

            return _db.AccountSetupTokens
                .Where(t => t.UserId == userId && t.CompanyId == companyId && t.ConsumedAt == null && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, nowUtc), cancellationToken);
        }

        public Task<AccountSetupToken?> FindUsableTokenAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var companyId = CompanyId;

            return _db.AccountSetupTokens.AsNoTracking().FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash && t.CompanyId == companyId && t.ConsumedAt == null && t.RevokedAt == null && t.ExpiresAt > nowUtc,
                cancellationToken);
        }

        public async Task<bool> TryConsumeTokenAsync(Guid tokenId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var companyId = CompanyId;

            // One conditional UPDATE: the row is claimed by exactly one caller, whatever else is running.
            var claimed = await _db.AccountSetupTokens
                .Where(t => t.Id == tokenId && t.CompanyId == companyId && t.ConsumedAt == null && t.RevokedAt == null && t.ExpiresAt > nowUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, nowUtc), cancellationToken);

            return claimed == 1;
        }

        public Task<int> RevokeSessionsAsync(Guid userId, string? exceptTokenHash, CancellationToken cancellationToken = default)
            => _db.Sessions
                .Where(s => s.UserId == userId && (exceptTokenHash == null || s.TokenHash != exceptTokenHash))
                .ExecuteDeleteAsync(cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);
    }
}
