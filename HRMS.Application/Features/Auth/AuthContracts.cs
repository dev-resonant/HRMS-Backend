namespace HRMS.Application.Features.Auth
{
    /// <summary>Body of POST /api/auth/change-password.</summary>
    public sealed class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;

        /// <summary>At least 12 characters and at most 72 bytes; must differ from the current password.</summary>
        public string NewPassword { get; set; } = string.Empty;
    }

    /// <summary>Body of POST /api/auth/set-password (anonymous; the one-time token is the credential).</summary>
    public sealed class SetPasswordRequest
    {
        /// <summary>The one-time token from the invitation.</summary>
        public string Token { get; set; } = string.Empty;

        public string NewPassword { get; set; } = string.Empty;
    }
}
