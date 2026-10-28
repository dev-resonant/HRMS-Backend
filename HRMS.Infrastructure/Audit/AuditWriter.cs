using HRMS.Application.Audit;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Audit
{
    public sealed class AuditWriter : IAuditWriter, IAuditStagingState
    {
        // audit_log.ip_address is VARCHAR(45); HttpClientInfo already guarantees this, the writer does not trust it blindly.
        private const int MaxIpAddressLength = 45;

        private readonly HrmsDbContext _dbContext;
        private readonly DbContextOptions<HrmsDbContext> _options;
        private readonly ICurrentUser _currentUser;
        private readonly ITenantContext _tenantContext;
        private readonly IClientInfo _clientInfo;

        public AuditWriter(
            HrmsDbContext dbContext,
            DbContextOptions<HrmsDbContext> options,
            ICurrentUser currentUser,
            ITenantContext tenantContext,
            IClientInfo clientInfo)
        {
            _dbContext = dbContext;
            _options = options;
            _currentUser = currentUser;
            _tenantContext = tenantContext;
            _clientInfo = clientInfo;
        }

        public void Stage(AuditEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            // Both properties throw when no authenticated, consistent current user has been loaded: no actor, no audit row.
            var userId = _currentUser.UserId;
            var companyId = _currentUser.CompanyId;

            // Only added to the tracked context. The caller's own SaveChanges commits it together with the business change.
            _dbContext.AuditLogs.Add(Build(entry, userId, companyId));
            _staged.Add(new StagedAudit(entry.Module, entry.Action, entry.TargetId));
        }

        private readonly List<StagedAudit> _staged = new();

        /// <summary>Entries staged on this request's unit of work, in order; read by the transaction pipeline for audited requests.</summary>
        public IReadOnlyList<StagedAudit> Staged => _staged;

        public async Task RecordFailedLoginAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default)
        {
            if (userId == Guid.Empty || !_tenantContext.IsResolved || _tenantContext.CompanyId != companyId)
            {
                // The only unauthenticated write path is bound to the tenant resolved from the host.
                throw new InvalidOperationException("A failed login can only be audited for an account of the resolved tenant.");
            }

            var entry = new AuditEntry(
                AuditModules.Auth,
                AuditActions.LoginFailed,
                targetId: userId,
                newValue: new { reason = "invalid_password" });

            // A separate context (own connection and transaction): independent of whatever the current request has pending.
            await using var independent = new HrmsDbContext(_options);

            independent.AuditLogs.Add(Build(entry, userId, companyId));

            await independent.SaveChangesAsync(cancellationToken);
        }

        private AuditLog Build(AuditEntry entry, Guid userId, Guid companyId)
        {
            var ip = _clientInfo.IpAddress;

            return new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CompanyId = companyId,
                Module = entry.Module,
                Action = entry.Action,
                TargetId = entry.TargetId,
                OldValue = entry.OldValueJson,
                NewValue = entry.NewValueJson,
                IpAddress = string.IsNullOrWhiteSpace(ip) || ip.Length > MaxIpAddressLength ? null : ip,
                CreatedAt = DateTime.UtcNow
            };
        }
    }
}
