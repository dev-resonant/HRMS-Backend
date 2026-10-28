using System.Text;
using FluentValidation;

namespace HRMS.Application.Features.Auth
{
    /// <summary>
    /// The password rules applied when a password is SET (change-password, password setup). The specification states no policy
    /// beyond "bcrypt cost 12+", so this is a minimal, documented assumption: long enough, and short enough for BCrypt (which
    /// silently ignores everything after 72 bytes). Login deliberately keeps accepting what it accepted before.
    /// </summary>
    public static class PasswordPolicy
    {
        public const int MinLength = 12;

        /// <summary>BCrypt only uses the first 72 bytes; a longer password would be silently truncated, so it is refused instead.</summary>
        public const int MaxBytes = 72;

        public const string Description = "must be at least 12 characters and at most 72 bytes, contain no control characters, and not be blank.";

        public static bool IsAcceptable(string? password)
            => !string.IsNullOrWhiteSpace(password)
               && password.Length >= MinLength
               && Encoding.UTF8.GetByteCount(password) <= MaxBytes
               && !password.Any(char.IsControl);

        public static IRuleBuilderOptions<T, string> MeetsPasswordPolicy<T>(this IRuleBuilder<T, string> rule)
            => rule.Must(IsAcceptable).WithMessage("{PropertyName} " + Description);
    }
}
