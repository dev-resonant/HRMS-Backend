using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Audit
{
    /// <summary>
    /// Writes <see cref="SecurityEvent"/> rows on a separate context (own connection and transaction), so the record of a rejected attempt
    /// survives the rollback of the command that rejected it and can never be part of, or block, a password change.
    /// </summary>
    public sealed class SecurityEventWriter : ISecurityEventWriter
    {
        private const int MaxIpAddressLength = 45;

        private readonly DbContextOptions<HrmsDbContext> _options;
        private readonly ITenantContext _tenant;
        private readonly IClientInfo _clientInfo;
        private readonly TimeProvider _time;
        private readonly ILogger<SecurityEventWriter> _logger;

        public SecurityEventWriter(
            DbContextOptions<HrmsDbContext> options,
            ITenantContext tenant,
            IClientInfo clientInfo,
            TimeProvider time,
            ILogger<SecurityEventWriter> logger)
        {
            _options = options;
            _tenant = tenant;
            _clientInfo = clientInfo;
            _time = time;
            _logger = logger;
        }

        public async Task RecordAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            // Never fatal for the caller: the response to the client must not depend on the audit trail being writable.
            try
            {
                if (!_tenant.IsResolved)
                {
                    _logger.LogError("A security event ({EventType}) could not be recorded because no tenant is resolved.", entry.EventType);
                    return;
                }

                var ip = _clientInfo.IpAddress;

                await using var independent = new HrmsDbContext(_options);

                independent.SecurityEvents.Add(new SecurityEvent
                {
                    Id = Guid.NewGuid(),
                    CompanyId = _tenant.CompanyId,
                    UserId = entry.UserId,
                    EventType = entry.EventType,
                    Reason = entry.Reason,
                    IpAddress = string.IsNullOrWhiteSpace(ip) || ip.Length > MaxIpAddressLength ? null : ip,
                    CreatedAt = _time.GetUtcNow().UtcDateTime
                });

                // Not tied to the request's cancellation: a client that disconnects must not erase the record of its attempt.
                await independent.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("A security event ({EventType}) could not be recorded: {ExceptionType}.", entry.EventType, ex.GetType().Name);
            }
        }
    }
}
