namespace HRMS.Application.Interfaces
{
    public static class SecurityEventTypes
    {
        public const string SetPasswordFailed = "auth.set_password_failed";

        public const string ChangePasswordFailed = "auth.change_password_failed";
    }

    /// <summary>Fixed reason codes. Free text is never recorded, so an event can never carry a submitted value.</summary>
    public static class SecurityEventReasons
    {
        /// <summary>The setup token is unknown, expired, already used, revoked or belongs to another tenant (deliberately not told apart).</summary>
        public const string InvalidToken = "invalid_token";

        /// <summary>A valid token, but the account is no longer eligible (deactivated, or it already has a password).</summary>
        public const string AccountNotEligible = "account_not_eligible";

        public const string WrongCurrentPassword = "wrong_current_password";
    }

    /// <summary>What happened and, only when trusted server-side state proves it, to whom.</summary>
    public sealed record SecurityEventEntry(string EventType, string Reason, Guid? UserId = null);

    /// <summary>
    /// Records rejected security-relevant attempts (see <c>SecurityEvent</c>). The tenant comes from the resolved host and the address
    /// from <see cref="IClientInfo"/>, never from the request body. The write uses its own database connection: it must survive the
    /// rollback of the failed command, and a failure to write it must never change the response (it is logged, without secrets).
    /// </summary>
    public interface ISecurityEventWriter
    {
        Task RecordAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default);
    }
}
