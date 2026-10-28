using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HRMS.Tests.Persistence;

/// <summary>
/// AddAccountSetupTokensAndEmployeeContactFields is purely additive. Proven through the real EF migration runner on a throwaway
/// database (inside the guarded Testcontainers instance) that already contains employees: nothing existing changes, exactly the
/// documented objects appear, the integrity constraints work, and rolling back removes only what the migration added.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountOnboardingMigrationTests
{
    private const string Before = "20261021122048_EmployeeConcurrencyToken";
    private const string After = "20261021130210_AddAccountSetupTokensAndEmployeeContactFields";

    private readonly PostgresFixture _fx;

    public AccountOnboardingMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<(Guid Company, Guid User, Guid Employee)> SeedLegacyAsync(ScratchDatabase database)
    {
        var company = Guid.NewGuid();
        var department = Guid.NewGuid();
        var user = Guid.NewGuid();
        var employee = Guid.NewGuid();

        await database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{company}','Co','legacy.test','','Asia/Kolkata',true,now());" +
            $"insert into \"Departments\"(\"Id\",\"CompanyId\",\"Name\",\"CreatedAt\") values ('{department}','{company}','Eng',now());" +
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"IsFirstLogin\",\"CreatedAt\") " +
            $"values ('{user}','{company}','5a000000-0000-4000-8000-000000000001','Legacy','legacy@legacy.test','x',true,false,now());" +
            "insert into \"Employees\"(\"Id\",\"UserId\",\"CompanyId\",\"DepartmentId\",\"EmpCode\",\"Designation\",\"JoiningDate\",\"Status\",\"CreatedAt\") " +
            $"values ('{employee}','{user}','{company}','{department}','EMP-0001','Engineer','2024-01-01',1,now());");

        return (company, user, employee);
    }

    private static Task<List<string>> SchemaAsync(ScratchDatabase d) => d.QueryAsync(
        "select 'C:' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') from information_schema.columns where table_schema = 'public' " +
        "union all select 'I:' || indexname || '|' || indexdef from pg_indexes where schemaname = 'public' " +
        "union all select 'K:' || conrelid::regclass::text || '|' || conname || '|' || contype::text from pg_constraint where connamespace = 'public'::regnamespace " +
        "order by 1");

    private static Task<List<string>> DataAsync(ScratchDatabase d) => d.QueryAsync(
        "select e.\"Id\"::text || e.\"EmpCode\" || e.\"Designation\" || e.\"Status\"::text || u.\"Email\" || u.\"IsActive\"::text from \"Employees\" e join \"Users\" u on u.\"Id\" = e.\"UserId\" order by 1");

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
    public async Task It_adds_exactly_the_documented_objects_and_changes_no_existing_row_or_column()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        await SeedLegacyAsync(database);
        var schemaBefore = await SchemaAsync(database);
        var dataBefore = await DataAsync(database);

        await MigrateToAsync(database, After);

        Assert.Equal(dataBefore, await DataAsync(database));                                   // existing rows untouched
        var added = (await SchemaAsync(database)).Except(schemaBefore).ToList();
        Assert.Empty(schemaBefore.Except(await SchemaAsync(database)));                         // nothing was removed or altered

        // The three nullable employee columns...
        Assert.Contains("C:Employees.EmploymentType|integer|YES|-", added);
        Assert.Contains("C:Employees.PersonalEmail|character varying|YES|-", added);
        Assert.Contains("C:Employees.Phone|character varying|YES|-", added);
        // ...and the new table with its hash index, FKs and primary key, and nothing else.
        Assert.Contains(added, a => a.StartsWith("C:AccountSetupTokens.TokenHash|character varying|NO"));
        Assert.Contains(added, a => a.StartsWith("I:IX_AccountSetupTokens_TokenHash|CREATE UNIQUE INDEX"));
        Assert.Contains(added, a => a.Contains("FK_AccountSetupTokens_Users_UserId"));
        Assert.Contains(added, a => a.Contains("FK_AccountSetupTokens_Companies_CompanyId"));
        Assert.All(added, a => Assert.True(a.Contains("AccountSetupTokens") || a.StartsWith("C:Employees.EmploymentType")
            || a.StartsWith("C:Employees.PersonalEmail") || a.StartsWith("C:Employees.Phone"), $"unexpected schema change: {a}"));

        // Existing employees simply have no value for the new optional fields.
        Assert.Equal("1", (await database.QueryAsync("select count(*) from \"Employees\" where \"PersonalEmail\" is null and \"Phone\" is null and \"EmploymentType\" is null")).Single());
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"AccountSetupTokens\"")).Single());
    }

    [Fact]
    public async Task The_token_table_enforces_unique_hashes_and_real_owners()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        var (company, user, _) = await SeedLegacyAsync(database);
        string Insert(Guid id, string hash, Guid owner) =>
            $"insert into \"AccountSetupTokens\"(\"Id\",\"CompanyId\",\"UserId\",\"TokenHash\",\"ExpiresAt\",\"CreatedAt\") values ('{id}','{company}','{owner}','{hash}',now() + interval '1 day',now())";
        var hash = new string('A', 64);
        await database.ExecuteAsync(Insert(Guid.NewGuid(), hash, user));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), hash, user)));
        var orphan = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), new string('B', 64), Guid.NewGuid())));
        var protectedUser = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync($"delete from \"Users\" where \"Id\" = '{user}'"));

        Assert.Contains("IX_AccountSetupTokens_TokenHash", Messages(duplicate));                // one hash, one token
        Assert.Contains("FK_AccountSetupTokens_Users_UserId", Messages(orphan));                // no token for a non-existent account
        Assert.Contains("AccountSetupTokens", Messages(protectedUser));                          // an account with tokens cannot silently vanish
    }

    [Fact]
    public async Task Rolling_it_back_removes_only_what_it_added_and_applying_it_again_works()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var schemaBefore = await SchemaAsync(database);
        await SeedLegacyAsync(database);
        var dataBefore = await DataAsync(database);
        await MigrateToAsync(database, After);

        await MigrateToAsync(database, Before);

        Assert.Equal(schemaBefore, await SchemaAsync(database));                                // exactly the earlier schema
        Assert.Equal(dataBefore, await DataAsync(database));
        Assert.DoesNotContain(After, await database.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\""));

        await MigrateToAsync(database, After);
        Assert.Equal(dataBefore, await DataAsync(database));
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"AccountSetupTokens\"")).Single());
    }

    [Fact]
    public async Task Its_lock_timeout_guard_is_transaction_local_and_leaves_no_session_setting_behind()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);

        await MigrateToAsync(database, After);

        // SET LOCAL ends with the migration's transaction: a new connection has the server default again.
        Assert.NotEqual("15s", (await database.QueryAsync("show lock_timeout")).Single());
    }
}
