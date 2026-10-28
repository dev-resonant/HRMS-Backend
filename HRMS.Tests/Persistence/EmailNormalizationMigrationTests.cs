using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Proves the AddUserEmailNormalizedCheck migration against data that ALREADY EXISTS (an empty database proves nothing about
/// production safety). Each test uses a throwaway database inside the disposable container, migrated to the previous
/// migration, filled with legacy rows through raw SQL, then migrated forward through the EF migration runner.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailNormalizationMigrationTests
{
    private const string Before = "20261012114231_AlignPhase1Schema";
    private const string After = "20261014112003_AddUserEmailNormalizedCheck";

    private readonly PostgresFixture _fx;

    public EmailNormalizationMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<Guid[]> SeedCompanyAsync(ScratchDatabase database, string domain)
    {
        var company = Guid.NewGuid();
        var role = Guid.NewGuid();
        await database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{company}','Co','{domain}','','Asia/Kolkata',true,now());" +
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{role}','{company}','Admin','Administrator','{{}}',true,now());");
        return new[] { company, role };
    }

    // Bind parameters (not string concatenation) so that any character - tabs, newlines, NBSP, ideographic space - is stored exactly.
    private static Task SeedUserAsync(ScratchDatabase database, Guid[] companyAndRole, string email)
        => database.ExecuteAsync(
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"IsFirstLogin\",\"CreatedAt\") " +
            "values (gen_random_uuid(),@company,@role,'Legacy',@email,'x',true,false,now())",
            ("company", companyAndRole[0]), ("role", companyAndRole[1]), ("email", email));

    private static async Task<bool> ConstraintExistsAsync(ScratchDatabase database)
        => (await database.QueryAsync("select count(*) from pg_constraint where conname = 'CK_Users_Email_Normalized'")).Single() == "1";

    private static async Task<List<string>> AppliedAsync(ScratchDatabase database)
        => await database.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\" order by 1");

    private static string Messages(Exception ex)
    {
        var text = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            text.Add(current.Message);
            if (current.InnerException is null) break;
        }

        return string.Join(" | ", text);
    }

    [Fact]
    public async Task Migration_applies_to_existing_normalized_data_without_changing_any_row()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var a = await SeedCompanyAsync(database, "a.test");
        var b = await SeedCompanyAsync(database, "b.test");
        await SeedUserAsync(database, a, "admin@a.test");
        await SeedUserAsync(database, a, "second@a.test");
        await SeedUserAsync(database, b, "admin@a.test");                      // same email in another company is allowed
        var before = await database.QueryAsync("select \"CompanyId\"::text || ':' || \"Email\" from \"Users\" order by 1");

        await MigrateToAsync(database, After);

        Assert.True(await ConstraintExistsAsync(database));
        Assert.Contains(After, await AppliedAsync(database));
        Assert.Equal(before, await database.QueryAsync("select \"CompanyId\"::text || ':' || \"Email\" from \"Users\" order by 1"));
    }

    [Theory]
    [InlineData("Admin@Mixed.TEST")]
    [InlineData(" padded@example.test")]
    [InlineData("padded@example.test ")]
    public async Task Migration_aborts_without_touching_data_when_an_existing_email_is_not_normalized(string legacyEmail)
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "legacy.test");
        await SeedUserAsync(database, company, legacyEmail);
        await SeedUserAsync(database, company, "fine@legacy.test");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("AddUserEmailNormalizedCheck", Messages(failure));
        Assert.Contains("No data was changed", Messages(failure));
        Assert.False(await ConstraintExistsAsync(database));                                   // rolled back
        Assert.DoesNotContain(After, await AppliedAsync(database));                            // not recorded
        Assert.Equal(new[] { "fine@legacy.test", legacyEmail }.OrderBy(x => x, StringComparer.Ordinal),
            (await database.QueryAsync("select \"Email\" from \"Users\"")).OrderBy(x => x, StringComparer.Ordinal));   // nothing rewritten or deleted
    }

    [Fact]
    public async Task Migration_aborts_and_reports_emails_that_would_collide_after_normalization_without_deleting_either()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "collide.test");
        await SeedUserAsync(database, company, "dup@collide.test");
        await SeedUserAsync(database, company, "DUP@collide.test");                 // allowed by the old case-sensitive unique index

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("1 company/email group(s) would collide", Messages(failure));
        Assert.False(await ConstraintExistsAsync(database));
        Assert.Equal("2", (await database.QueryAsync("select count(*) from \"Users\"")).Single());
        Assert.Equal(new[] { "DUP@collide.test", "dup@collide.test" },
            (await database.QueryAsync("select \"Email\" from \"Users\"")).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Down_removes_only_the_constraint_and_keeps_every_row()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "down.test");
        await SeedUserAsync(database, company, "keep@down.test");
        await MigrateToAsync(database, After);
        Assert.True(await ConstraintExistsAsync(database));

        await MigrateToAsync(database, Before);

        Assert.False(await ConstraintExistsAsync(database));
        Assert.DoesNotContain(After, await AppliedAsync(database));
        Assert.Equal(new[] { "keep@down.test" }, await database.QueryAsync("select \"Email\" from \"Users\""));
        await SeedUserAsync(database, company, "Mixed@Down.TEST");                 // the old, case-sensitive behavior is restored
    }

    // ---- whitespace policy: the guard must reject what the application would trim, and nothing else -------------------

    private static async Task<List<string>> UserEmailsOrderedAsync(ScratchDatabase database)
        => (await database.QueryAsync("select \"Email\" from \"Users\"")).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData(0x0009, true)]      // tab, leading
    [InlineData(0x000A, false)]     // line feed, trailing
    [InlineData(0x000D, true)]      // carriage return, leading
    [InlineData(0x0085, false)]     // NEL, trailing
    [InlineData(0x00A0, true)]      // no-break space, leading
    [InlineData(0x2003, false)]     // em space (representative of U+2000-U+200A), trailing
    [InlineData(0x2028, false)]     // line separator, trailing
    [InlineData(0x3000, true)]      // ideographic space, leading
    public async Task Migration_aborts_without_touching_data_when_an_existing_email_has_edge_whitespace_the_application_trims(int codePoint, bool leading)
    {
        var character = char.ConvertFromUtf32(codePoint);
        var legacyEmail = leading ? character + "padded@legacy.test" : "padded@legacy.test" + character;

        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "ws.test");
        await SeedUserAsync(database, company, legacyEmail);
        await SeedUserAsync(database, company, "fine@legacy.test");
        var before = await UserEmailsOrderedAsync(database);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("AddUserEmailNormalizedCheck", Messages(failure));
        Assert.Contains("1 user email(s)", Messages(failure));
        Assert.Contains("No data was changed", Messages(failure));
        Assert.False(await ConstraintExistsAsync(database));
        Assert.DoesNotContain(After, await AppliedAsync(database));
        Assert.Equal(before, await UserEmailsOrderedAsync(database));              // identical bytes: nothing rewritten, nothing deleted
        Assert.Contains(legacyEmail, await UserEmailsOrderedAsync(database));
    }

    [Fact]
    public async Task Migration_counts_every_trimmed_whitespace_character_at_both_ends_and_still_changes_nothing()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "allws.test");

        var inserted = 0;
        foreach (var codePoint in HRMS.Infrastructure.Persistence.UserEmailPolicy.TrimmedWhitespaceCodePoints)
        {
            var character = char.ConvertFromUtf32(codePoint);
            await SeedUserAsync(database, company, $"{character}lead{codePoint:x4}@legacy.test");      // distinct bases: no collisions
            await SeedUserAsync(database, company, $"trail{codePoint:x4}@legacy.test{character}");
            inserted += 2;
        }

        Assert.Equal(50, inserted);                                                 // 25 characters x 2 ends
        var before = await UserEmailsOrderedAsync(database);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("50 user email(s)", Messages(failure));
        Assert.Contains("0 company/email group(s)", Messages(failure));
        Assert.False(await ConstraintExistsAsync(database));
        Assert.Equal(before, await UserEmailsOrderedAsync(database));
    }

    [Fact]
    public async Task Migration_reports_a_collision_that_exists_only_after_trimming_whitespace_and_deletes_neither_row()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "wscollide.test");
        await SeedUserAsync(database, company, "same@wscollide.test");
        await SeedUserAsync(database, company, "\tsame@wscollide.test");           // distinct for the old unique index, equal after trimming
        var before = await UserEmailsOrderedAsync(database);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("1 user email(s)", Messages(failure));
        Assert.Contains("1 company/email group(s) would collide", Messages(failure));
        Assert.Equal(before, await UserEmailsOrderedAsync(database));
        Assert.False(await ConstraintExistsAsync(database));
    }

    [Fact]
    public async Task Migration_accepts_existing_rows_that_the_application_would_leave_unchanged()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var company = await SeedCompanyAsync(database, "unchanged.test");
        foreach (var email in new[]
        {
            "plain@unchanged.test",
            "inner space@unchanged.test",              // inner whitespace is not touched by either side
            "inner\ttab@unchanged.test",
            "​zero-width-lead@unchanged.test",    // U+200B / U+FEFF are not whitespace for string.Trim()
            "zero-width-trail@unchanged.test﻿"
        })
        {
            Assert.Equal(email, HRMS.Domain.Common.EmailNormalizer.Normalize(email));
            await SeedUserAsync(database, company, email);
        }

        var before = await UserEmailsOrderedAsync(database);

        await MigrateToAsync(database, After);

        Assert.True(await ConstraintExistsAsync(database));
        Assert.Contains(After, await AppliedAsync(database));
        Assert.Equal(before, await UserEmailsOrderedAsync(database));
    }
}
