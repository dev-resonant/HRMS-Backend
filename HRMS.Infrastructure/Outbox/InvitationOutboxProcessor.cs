using System.Data;
using HRMS.Application.Features.Auth;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace HRMS.Infrastructure.Outbox
{
    /// <summary>
    /// Delivers the invitations recorded in <c>OutboxMessages</c>. Everything here runs OUTSIDE the request transaction.
    /// <para>
    /// Claiming: one <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING</c> marks due messages as Processing for a lease
    /// and increments their attempt counter, so concurrent workers (threads or instances) never receive the same message under normal
    /// operation. A worker that dies keeps its claim only until the lease ends; the next worker then takes the message over.
    /// </para>
    /// <para>
    /// The raw token: it exists only inside <see cref="ProcessClaimedAsync"/>. The worker generates it, replaces the hash of the pending
    /// (never delivered) token row with its hash, and hands it to the channel. Nothing that is stored can reproduce it. If the channel
    /// then fails, the next attempt simply rotates again; a delivered-but-unacknowledged message is sent again with a newer token and
    /// the older link stops working (delivery is at-least-once, never silently lost).
    /// </para>
    /// <para>
    /// Failures are recorded as the exception TYPE only (a message could contain the recipient address) and retried with exponential
    /// backoff up to <see cref="OutboxOptions.MaxAttempts"/>; after that the message is terminal (<c>Failed</c>) and logged at error level.
    /// </para>
    /// </summary>
    public sealed class InvitationOutboxProcessor
    {
        private readonly HrmsDbContext _db;
        private readonly IAccountInvitationSender _sender;
        private readonly ITokenHasher _tokenHasher;
        private readonly TimeProvider _time;
        private readonly OutboxOptions _options;
        private readonly ILogger<InvitationOutboxProcessor> _logger;

        public InvitationOutboxProcessor(
            HrmsDbContext db,
            IAccountInvitationSender sender,
            ITokenHasher tokenHasher,
            TimeProvider time,
            OutboxOptions options,
            ILogger<InvitationOutboxProcessor> logger)
        {
            _db = db;
            _sender = sender;
            _tokenHasher = tokenHasher;
            _time = time;
            _options = options;
            _logger = logger;
        }

        private sealed record Claimed(Guid Id, Guid CompanyId, Guid UserId, Guid SetupTokenId, int Attempts);

        /// <summary>Processes up to one batch of due messages; returns how many were claimed.</summary>
        public Task<int> ProcessDueAsync(CancellationToken cancellationToken) => ProcessAsync(null, cancellationToken);

        /// <summary>Tries this one message if it is due and unclaimed; returns 1 when it was claimed, otherwise 0.</summary>
        public Task<int> ProcessOneAsync(Guid messageId, CancellationToken cancellationToken) => ProcessAsync(messageId, cancellationToken);

        private async Task<int> ProcessAsync(Guid? onlyId, CancellationToken cancellationToken)
        {
            if (!_sender.IsConfigured)
            {
                // Nothing can be delivered: keep the messages untouched (no attempt is burned) until a channel is configured.
                return 0;
            }

            var now = _time.GetUtcNow().UtcDateTime;

            await AbandonStuckAsync(now, cancellationToken);

            var claimed = await ClaimAsync(onlyId, now, cancellationToken);

            foreach (var message in claimed)
            {
                try
                {
                    await ProcessClaimedAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ReleaseAsync(message);                                    // shutting down: give the message back untouched
                    throw;
                }
                catch (Exception ex)
                {
                    // Unexpected (for example the database went away mid-way). The claim lease protects the message; log the type only.
                    _logger.LogError("Processing of outbox message {MessageId} failed unexpectedly with {ExceptionType}; it is retried after its claim expires.", message.Id, ex.GetType().Name);
                }
            }

            return claimed.Count;
        }

        /// <summary>A claim that expired while the attempt budget is already spent will never be retried: make that terminal and visible.</summary>
        private async Task AbandonStuckAsync(DateTime now, CancellationToken cancellationToken)
        {
            var max = _options.MaxAttempts;

            var abandoned = await _db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Processing && m.LockedUntil < now && m.Attempts >= max)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Failed)
                    .SetProperty(m => m.ProcessedAt, now)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null)
                    .SetProperty(m => m.LastError, "abandoned"), cancellationToken);

            if (abandoned > 0)
            {
                _logger.LogError("{Count} outbox message(s) were abandoned after {MaxAttempts} attempts without a recorded result and are now terminal (Failed).", abandoned, max);
            }
        }

        private async Task<List<Claimed>> ClaimAsync(Guid? onlyId, DateTime now, CancellationToken cancellationToken)
        {
            const string sql = """
                UPDATE "OutboxMessages" AS m
                SET "Status" = 'Processing', "LockedUntil" = @lease, "Attempts" = m."Attempts" + 1
                WHERE m."Id" IN (
                    SELECT c."Id" FROM "OutboxMessages" AS c
                    WHERE c."Kind" = 'account-invitation'
                      AND ((c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                        OR (c."Status" = 'Processing' AND c."LockedUntil" < @now AND c."Attempts" < @max))
                      AND (@only IS NULL OR c."Id" = @only)
                    ORDER BY c."NextAttemptAt"
                    LIMIT @limit
                    FOR UPDATE SKIP LOCKED)
                RETURNING m."Id", m."CompanyId", m."UserId", m."SetupTokenId", m."Attempts"
                """;

            var connection = _db.Database.GetDbConnection();
            await _db.Database.OpenConnectionAsync(cancellationToken);

            try
            {
                await using var command = (NpgsqlCommand)connection.CreateCommand();
                command.CommandText = sql;
                command.Transaction = (NpgsqlTransaction?)_db.Database.CurrentTransaction?.GetDbTransaction();
                command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = now + _options.ClaimLease });
                command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
                command.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = _options.MaxAttempts });
                command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Uuid) { Value = onlyId.HasValue ? onlyId.Value : DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = onlyId.HasValue ? 1 : _options.BatchSize });

                var result = new List<Claimed>();

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new Claimed(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetInt32(4)));
                }

                return result;
            }
            finally
            {
                await _db.Database.CloseConnectionAsync();
            }
        }

        private async Task ProcessClaimedAsync(Claimed message, CancellationToken cancellationToken)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var account = await _db.Users.AsNoTracking()
                .Where(u => u.Id == message.UserId && u.CompanyId == message.CompanyId)
                .Select(u => new { u.Name, u.Email, u.IsActive, u.IsFirstLogin })
                .FirstOrDefaultAsync(cancellationToken);

            var domain = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == message.CompanyId)
                .Select(c => c.Domain)
                .FirstOrDefaultAsync(cancellationToken);

            var token = await _db.AccountSetupTokens.AsNoTracking()
                .Where(t => t.Id == message.SetupTokenId && t.CompanyId == message.CompanyId && t.UserId == message.UserId)
                .Select(t => new { t.ExpiresAt, t.ConsumedAt, t.RevokedAt })
                .FirstOrDefaultAsync(cancellationToken);

            // The invitation may have become pointless since it was recorded (used, replaced by a newer one, expired, account changed).
            var obsolete =
                account is null || domain is null ? "account_missing"
                : !account.IsActive ? "account_inactive"
                : !account.IsFirstLogin ? "account_already_set_up"
                : token is null || token.ConsumedAt is not null || token.RevokedAt is not null ? "token_not_usable"
                : token.ExpiresAt <= now ? "token_expired"
                : null;

            if (obsolete is not null)
            {
                await FinishAsync(message, OutboxStatus.Cancelled, obsolete);
                _logger.LogInformation("Outbox message {MessageId} was cancelled ({Reason}); nothing was sent.", message.Id, obsolete);
                return;
            }

            // Mint the raw token now. It replaces the hash of the pending token row; only a still-open, unexpired row is touched, so a
            // token that was consumed or revoked in the meantime can never be revived by a delayed delivery.
            var rawToken = AccountInvitationIssuer.NewToken();
            var newHash = _tokenHasher.Hash(rawToken);

            var rotated = await _db.AccountSetupTokens
                .Where(t => t.Id == message.SetupTokenId && t.CompanyId == message.CompanyId && t.ConsumedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.TokenHash, newHash), cancellationToken);

            if (rotated != 1)
            {
                await FinishAsync(message, OutboxStatus.Cancelled, "token_not_usable");
                _logger.LogInformation("Outbox message {MessageId} was cancelled (token_not_usable); nothing was sent.", message.Id);
                return;
            }

            var invitation = new AccountInvitation(message.CompanyId, message.UserId, account!.Name, account.Email, domain!, rawToken, token!.ExpiresAt);

            try
            {
                // Bounded by the claim lease so a hung channel cannot keep a message claimed past the point another worker takes it over.
                using var sendLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendLimit.CancelAfter(TimeSpan.FromTicks(_options.ClaimLease.Ticks * 4 / 5));

                await _sender.SendAsync(invitation, sendLimit.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(message, ex.GetType().Name);
                return;
            }

            await FinishAsync(message, OutboxStatus.Sent, null);
            _logger.LogInformation("Outbox message {MessageId} was delivered on attempt {Attempt}.", message.Id, message.Attempts);
        }

        private async Task RecordFailureAsync(Claimed message, string exceptionType)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            if (message.Attempts >= _options.MaxAttempts)
            {
                await FinishAsync(message, OutboxStatus.Failed, exceptionType);

                _logger.LogError(
                    "Delivery of outbox message {MessageId} for user {UserId} failed with {ExceptionType} on its last attempt ({Attempt} of {MaxAttempts}); " +
                    "the message is terminal (Failed) and the invitation has to be re-issued.",
                    message.Id, message.UserId, exceptionType, message.Attempts, _options.MaxAttempts);

                return;
            }

            var next = now + _options.DelayAfter(message.Attempts);

            await _db.OutboxMessages
                .Where(m => m.Id == message.Id && m.Status == OutboxStatus.Processing && m.Attempts == message.Attempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.NextAttemptAt, next)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null)
                    .SetProperty(m => m.LastError, exceptionType), CancellationToken.None);

            _logger.LogWarning(
                "Delivery of outbox message {MessageId} (attempt {Attempt} of {MaxAttempts}) failed with {ExceptionType}; the next attempt is at {NextAttemptAt:O}.",
                message.Id, message.Attempts, _options.MaxAttempts, exceptionType, next);
        }

        /// <summary>Terminal state. Guarded by the claim (status and attempt number) so a worker that lost its lease cannot overwrite the new owner's result.</summary>
        private async Task FinishAsync(Claimed message, string status, string? error)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            await _db.OutboxMessages
                .Where(m => m.Id == message.Id && m.Status == OutboxStatus.Processing && m.Attempts == message.Attempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, status)
                    .SetProperty(m => m.ProcessedAt, now)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null)
                    .SetProperty(m => m.LastError, error), CancellationToken.None);
        }

        /// <summary>Returns a claimed message to the queue without counting the attempt (the worker is shutting down).</summary>
        private async Task ReleaseAsync(Claimed message)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            await _db.OutboxMessages
                .Where(m => m.Id == message.Id && m.Status == OutboxStatus.Processing && m.Attempts == message.Attempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.Attempts, m => m.Attempts - 1)
                    .SetProperty(m => m.NextAttemptAt, now)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null), CancellationToken.None);
        }
    }
}
