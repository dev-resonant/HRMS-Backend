using HRMS.Domain.Common;

namespace HRMS.Domain.Entities
{
    /// <summary>
    /// A one-time, short-lived credential that lets the holder set the password of ONE account (onboarding). Only the SHA-256 hash
    /// of the random token is stored; the token itself exists only in memory while it is handed to the delivery channel.
    /// Usable exactly while it is not expired, not consumed and not revoked.
    /// </summary>
    public class AccountSetupToken : BaseEntity
    {
        public Guid CompanyId { get; set; }

        public Guid UserId { get; set; }

        /// <summary>SHA-256 of the token, upper-case hex (64 characters). Unique.</summary>
        public string TokenHash { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        /// <summary>Set when the token was used to set a password. A consumed token can never be used again.</summary>
        public DateTime? ConsumedAt { get; set; }

        /// <summary>Set when a newer invitation replaced this one.</summary>
        public DateTime? RevokedAt { get; set; }

        public User User { get; set; } = null!;

        public Company Company { get; set; } = null!;
    }
}
