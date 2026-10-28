using HRMS.Domain.Common;

namespace HRMS.Domain.Entities
{
    /// <summary>Lifecycle of an <see cref="OutboxMessage"/>. Stored as text (see the table's check constraint).</summary>
    public static class OutboxStatus
    {
        /// <summary>Waiting for its first or next delivery attempt (<see cref="OutboxMessage.NextAttemptAt"/>).</summary>
        public const string Pending = "Pending";

        /// <summary>Claimed by one worker until <see cref="OutboxMessage.LockedUntil"/>; a crashed worker's claim expires and is retaken.</summary>
        public const string Processing = "Processing";

        /// <summary>Handed to the delivery channel. Terminal.</summary>
        public const string Sent = "Sent";

        /// <summary>Retries exhausted (or the claim was abandoned too often). Terminal: an administrator must re-issue the invitation.</summary>
        public const string Failed = "Failed";

        /// <summary>No longer needed: the token was used, revoked or expired, or the account is not awaiting setup. Terminal.</summary>
        public const string Cancelled = "Cancelled";

        public static readonly IReadOnlyList<string> All = new[] { Pending, Processing, Sent, Failed, Cancelled };
    }

    /// <summary>
    /// The durable intent to deliver ONE account invitation, written in the same database transaction as the account and its
    /// <see cref="AccountSetupToken"/>. It deliberately holds NO secret: not the token, not an encrypted token, not the recipient
    /// address. The delivery worker mints the raw token at send time (it replaces the hash of the pending, never-delivered token)
    /// and reads the recipient from the account, so reading this table never yields anything that can be used to log in.
    /// A rolled-back transaction leaves no row, hence nothing to deliver.
    /// </summary>
    public class OutboxMessage : BaseEntity
    {
        public const string AccountInvitationKind = "account-invitation";

        public Guid CompanyId { get; set; }

        public string Kind { get; set; } = AccountInvitationKind;

        public Guid UserId { get; set; }

        /// <summary>The token this message delivers. Unique: one message per token is the idempotency key.</summary>
        public Guid SetupTokenId { get; set; }

        public string Status { get; set; } = OutboxStatus.Pending;

        /// <summary>Delivery attempts started so far (incremented when a worker claims the message).</summary>
        public int Attempts { get; set; }

        public DateTime NextAttemptAt { get; set; }

        public DateTime? LockedUntil { get; set; }

        /// <summary>When the message reached a terminal status.</summary>
        public DateTime? ProcessedAt { get; set; }

        /// <summary>Short machine code or exception TYPE name of the last failure. Never a message, address or token.</summary>
        public string? LastError { get; set; }

        public User User { get; set; } = null!;

        public Company Company { get; set; } = null!;

        public AccountSetupToken SetupToken { get; set; } = null!;
    }
}
