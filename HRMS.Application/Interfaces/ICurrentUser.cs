namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// The authenticated caller for the current request, rebuilt from the database on every protected request (decision D5):
    /// the JWT supplies only the identity, never the role or permissions. Every property fails closed - it throws
    /// <see cref="UnauthorizedAccessException"/> when the context has not been (successfully) loaded.
    /// </summary>
    public interface ICurrentUser
    {
        bool IsResolved { get; }

        Guid UserId { get; }

        Guid CompanyId { get; }

        /// <summary>Stable role key, e.g. employee, manager, hr_admin, super_admin.</summary>
        string RoleKey { get; }

        string RoleDisplayName { get; }

        /// <summary>The caller's employee record, or null when the account has none (e.g. a bootstrap administrator).</summary>
        Guid? EmployeeId { get; }

        /// <summary>
        /// True while the account must still set a password (specification: is_first_login). Such a caller may only use the
        /// self-service endpoints that explicitly allow it (change-password, me, logout); the authorization filter enforces this.
        /// </summary>
        bool PasswordChangeRequired { get; }

        /// <summary>
        /// True only when the caller's company is THE designated platform company (<c>Companies.IsPlatform</c>) and exactly one company is
        /// designated. Read from the database on every protected request - never from the token, a header, the request body or the role name.
        /// False when no company is designated, when the state is ambiguous, or when the caller belongs to an ordinary company.
        /// </summary>
        bool IsPlatform { get; }

        IReadOnlyDictionary<string, string[]> Permissions { get; }

        /// <summary>True only when the role grants this catalog permission. Unknown or missing permissions deny.</summary>
        bool HasPermission(string module, string action);
    }
}
