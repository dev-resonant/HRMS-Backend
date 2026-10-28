using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserEmailNormalizedCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing behind long-running queries (transaction-local, resets at commit).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            // SAFETY GUARD - this migration never rewrites, merges or deletes user rows. If any existing email is not already
            // normalized, the migration aborts (and the whole transaction rolls back) with a message that reports counts only.
            // "Normalized" means exactly what the application's EmailNormalizer produces: lowercase, with none of the
            // characters string.Trim() removes (char.IsWhiteSpace) at either end. PostgreSQL's btrim(x) alone removes only the
            // plain space, so the trim set is passed explicitly - the same list as UserEmailPolicy and the check constraint below.
            // Resolve offending rows manually (decide which account is the real one when two differ only by case or padding),
            // then re-run. Production preflight queries are documented in docs/PROJECT_STATUS.md.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    trim_chars constant text := E'\u0009\u000a\u000b\u000c\u000d \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000';
    not_normalized integer;
    would_collide integer;
BEGIN
    SELECT count(*) INTO not_normalized FROM ""Users"" WHERE ""Email"" <> lower(btrim(""Email"", trim_chars));

    SELECT count(*) INTO would_collide FROM (
        SELECT 1 FROM ""Users"" GROUP BY ""CompanyId"", lower(btrim(""Email"", trim_chars)) HAVING count(*) > 1
    ) duplicates;

    IF not_normalized > 0 OR would_collide > 0 THEN
        RAISE EXCEPTION 'AddUserEmailNormalizedCheck: % user email(s) are not normalized (uppercase letters, or leading/trailing whitespace that the application trims) and % company/email group(s) would collide after normalization. No data was changed. Resolve these rows manually, then re-run the migration.', not_normalized, would_collide;
    END IF;
END $$;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Email_Normalized",
                table: "Users",
                sql: "\"Email\" = lower(btrim(\"Email\", E'\\u0009\\u000a\\u000b\\u000c\\u000d \\u0085\\u00a0\\u1680\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Email_Normalized",
                table: "Users");
        }
    }
}