using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Work to run only AFTER the request's database transaction has committed (sending an invitation, for example). It is discarded
    /// if the transaction rolls back, and a failing action never changes the outcome of the committed operation: the failure is
    /// logged (type only) and the remaining actions still run.
    /// </summary>
    public interface IPostCommitActions
    {
        void Enqueue(string name, Func<CancellationToken, Task> action);
    }

    /// <summary>What a delivery channel receives. The raw token exists only here and in the channel; it is never stored, logged or audited.</summary>
    public sealed record AccountInvitation(
        Guid CompanyId,
        Guid UserId,
        string RecipientName,
        string RecipientEmail,
        string CompanyDomain,
        string Token,
        DateTime ExpiresAtUtc)
    {
        // A record prints every property; make sure the token can never reach a log through string formatting.
        public sealed override string ToString() => $"AccountInvitation {{ UserId = {UserId}, ExpiresAtUtc = {ExpiresAtUtc:O} }}";
    }

    /// <summary>
    /// Delivers a one-time password-setup token to a new employee (the "welcome email" of specification 5.1 step 4). No provider is
    /// bundled or assumed: the default implementation reports <see cref="IsConfigured"/> = false and delivers nothing.
    /// </summary>
    public interface IAccountInvitationSender
    {
        bool IsConfigured { get; }

        Task SendAsync(AccountInvitation invitation, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Asks the delivery worker to try one outbox message right now instead of waiting for its next poll. Purely an optimisation: the
    /// message is already durable, so a failure here (or a missing worker) only delays delivery. Never throws for a delivery problem.
    /// </summary>
    public interface IInvitationDispatcher
    {
        Task NudgeAsync(Guid messageId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Settings of the invitation outbox (appsettings section "Outbox"). Failure policy: a message is attempted at most
    /// <see cref="MaxAttempts"/> times, waiting <c>BaseDelay * 2^(attempt-1)</c> (capped at <see cref="MaxDelay"/>) between attempts;
    /// after the last failed attempt it becomes terminal (<c>Failed</c>), is logged at error level and is never retried again.
    /// </summary>
    public sealed class OutboxOptions
    {
        public const string SectionName = "Outbox";

        /// <summary>Run the background worker that retries pending messages. Turn off to deliver only through the inline nudge.</summary>
        public bool WorkerEnabled { get; set; } = true;

        /// <summary>Try a message right after the request that created it committed (in addition to the worker).</summary>
        public bool InlineDelivery { get; set; } = true;

        public int MaxAttempts { get; set; } = 6;

        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

        /// <summary>How long one worker owns a claimed message; if it dies, another worker takes the message over after this.</summary>
        public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

        public int BatchSize { get; set; } = 20;

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Delay before attempt number <paramref name="attemptsAlreadyMade"/> + 1 (1 = after the first failure).</summary>
        public TimeSpan DelayAfter(int attemptsAlreadyMade)
        {
            var exponent = Math.Clamp(attemptsAlreadyMade - 1, 0, 30);
            var ticks = BaseDelay.Ticks * Math.Pow(2, exponent);

            return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
        }
    }

    /// <summary>Settings of the password-setup invitation (appsettings section "Onboarding").</summary>
    public sealed class AccountSetupOptions
    {
        public const string SectionName = "Onboarding";

        public const int DefaultInvitationLifetimeHours = 72;

        /// <summary>How long an invitation token stays valid. The specification does not define it (assumption, see docs).</summary>
        public TimeSpan InvitationLifetime { get; set; } = TimeSpan.FromHours(DefaultInvitationLifetimeHours);
    }

    /// <summary>Account and token access for the onboarding and password flows. Everything is bound to the tenant resolved from the host.</summary>
    public interface IAccountRepository
    {
        /// <summary>Tracked user of the resolved tenant, or null.</summary>
        Task<User?> GetUserForUpdateAsync(Guid userId, CancellationToken cancellationToken = default);

        void AddToken(AccountSetupToken token);

        /// <summary>Stages the delivery intent for a token; it is saved (or rolled back) with the account and the token.</summary>
        void AddOutboxMessage(OutboxMessage message);

        /// <summary>Marks every unused, unrevoked token of the user as revoked; returns how many were open.</summary>
        Task<int> RevokeOpenTokensAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken = default);

        /// <summary>The usable token (not expired, consumed or revoked) with this hash in the resolved tenant, or null.</summary>
        Task<AccountSetupToken?> FindUsableTokenAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically consumes the token: true for exactly one caller even under concurrency (a conditional UPDATE), false if it was
        /// already used, revoked or expired in the meantime.
        /// </summary>
        Task<bool> TryConsumeTokenAsync(Guid tokenId, DateTime nowUtc, CancellationToken cancellationToken = default);

        /// <summary>Deletes the user's sessions, optionally keeping the one with this token hash; returns how many were deleted.</summary>
        Task<int> RevokeSessionsAsync(Guid userId, string? exceptTokenHash, CancellationToken cancellationToken = default);

        Task SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
