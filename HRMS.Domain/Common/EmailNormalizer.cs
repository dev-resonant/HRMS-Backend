namespace HRMS.Domain.Common
{
    /// <summary>
    /// The single email normalization rule for user accounts: trim surrounding whitespace, then lowercase with the
    /// invariant culture. Used by login input, every seeder and the User.Email setter, and mirrored in the database by the
    /// CK_Users_Email_Normalized check constraint ("Email" = lower(btrim("Email"))).
    /// </summary>
    public static class EmailNormalizer
    {
        public static string Normalize(string email)
        {
            ArgumentNullException.ThrowIfNull(email);

            return email.Trim().ToLowerInvariant();
        }
    }
}
