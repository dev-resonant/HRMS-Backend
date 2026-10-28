namespace HRMS.Infrastructure.Persistence
{
    /// <summary>
    /// Database side of the user-email normalization policy. The application rule is
    /// <c>EmailNormalizer.Normalize</c> = <c>Trim()</c> + <c>ToLowerInvariant()</c>. <c>string.Trim()</c> removes every
    /// character for which <c>char.IsWhiteSpace</c> is true, but PostgreSQL's <c>btrim(x)</c> removes only the plain space,
    /// so the check constraint passes <see cref="TrimmedWhitespaceCodePoints"/> to <c>btrim</c> explicitly. Tests verify that
    /// this list equals the set of BMP characters that <c>char.IsWhiteSpace</c> reports, and that the migrated database rejects
    /// exactly those characters at either end of an email.
    ///
    /// NOTE: the migration <c>AddUserEmailNormalizedCheck</c> contains this SQL as a literal (migrations must stay
    /// self-contained); keep them identical.
    /// </summary>
    public static class UserEmailPolicy
    {
        public const string ConstraintName = "CK_Users_Email_Normalized";

        /// <summary>Every code point (all in the Basic Multilingual Plane) that <c>string.Trim()</c> removes.</summary>
        public static readonly IReadOnlyList<int> TrimmedWhitespaceCodePoints = new[]
        {
            0x0009, 0x000A, 0x000B, 0x000C, 0x000D,            // tab, LF, VT, FF, CR
            0x0020,                                            // space
            0x0085,                                            // NEL
            0x00A0,                                            // no-break space
            0x1680,                                            // Ogham space mark
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,    // en quad .. six-per-em space
            0x2006, 0x2007, 0x2008, 0x2009, 0x200A,            // .. figure, punctuation, thin, hair space
            0x2028, 0x2029,                                    // line / paragraph separator
            0x202F,                                            // narrow no-break space
            0x205F,                                            // medium mathematical space
            0x3000                                             // ideographic space
        };

        /// <summary>PostgreSQL E-string literal listing the trimmed characters, e.g. <c>E'\u0009\u000a...'</c>.</summary>
        public static string TrimCharactersSqlLiteral =>
            "E'" + string.Concat(TrimmedWhitespaceCodePoints.Select(c => c == 0x20 ? " " : $"\\u{c:x4}")) + "'";

        /// <summary>The check expression: already lowercase, and no trimmed whitespace at either end.</summary>
        public static string CheckConstraintSql =>
            $"\"Email\" = lower(btrim(\"Email\", {TrimCharactersSqlLiteral}))";
    }
}
