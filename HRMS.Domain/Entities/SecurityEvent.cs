using HRMS.Domain.Common;

namespace HRMS.Domain.Entities
{
    /// <summary>
    /// A security-relevant event that does NOT belong in the business audit log: a rejected attempt, typically anonymous. The audit log
    /// requires a real acting user (audit_log.user_id is a required foreign key, decision D6); an invalid password-setup token has no
    /// such user and inventing one would corrupt that guarantee. This table has a NULLABLE user instead. It is append-only (enforced by
    /// <c>HrmsDbContext</c>) and never holds a password, token, token hash, e-mail address or any submitted value.
    /// </summary>
    public class SecurityEvent : BaseEntity
    {
        /// <summary>The tenant resolved from the request host (the endpoints that record events cannot run without one).</summary>
        public Guid CompanyId { get; set; }

        /// <summary>The account the attempt concerned, when it is known from trusted server-side state; otherwise null.</summary>
        public Guid? UserId { get; set; }

        /// <summary>Dotted event name, for example <c>auth.set_password_failed</c>.</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Fixed machine-readable reason code, for example <c>invalid_token</c>. Never free text.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Client address as seen by the server (see the forwarded-header policy); null when unknown.</summary>
        public string? IpAddress { get; set; }

        public Company Company { get; set; } = null!;

        public User? User { get; set; }
    }
}
