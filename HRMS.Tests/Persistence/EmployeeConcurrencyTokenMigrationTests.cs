using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HRMS.Tests.Persistence;

/// <summary>
/// EmployeeConcurrencyToken is a snapshot-only migration (the token is PostgreSQL's built-in xmin system column). These tests prove,
/// through the real EF migration runner on a throwaway database that already holds employee data, that it really changes nothing
/// in the database, and that the token column is the system column and not a stored one.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmployeeConcurrencyTokenMigrationTests
{
    private const string Before = "20261016130045_AddSystemRoles";
    private const string After = "20261021122048_EmployeeConcurrencyToken";

    private readonly PostgresFixture _fx;

    public EmployeeConcurrencyTokenMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task SeedLegacyEmployeeAsync(ScratchDatabase database)
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
    }

    private static Task<List<string>> SchemaAsync(ScratchDatabase d) => d.QueryAsync(
        "select 'C:' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') from information_schema.columns where table_schema = 'public' " +
        "union all select 'I:' || indexname || '|' || indexdef from pg_indexes where schemaname = 'public' " +
        "union all select 'K:' || conrelid::regclass::text || '|' || conname || '|' || contype::text from pg_constraint where connamespace = 'public'::regnamespace " +
        "order by 1");

    private static Task<List<string>> DataAsync(ScratchDatabase d) => d.QueryAsync(
        "select e.\"Id\"::text || e.\"EmpCode\" || e.\"Designation\" || e.\"Status\"::text || u.\"Email\" || u.\"IsActive\"::text from \"Employees\" e join \"Users\" u on u.\"Id\" = e.\"UserId\" order by 1");

    private static Task<List<string>> AppliedAsync(ScratchDatabase d) => d.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\" order by 1");

    [Fact]
    public async Task Applying_it_to_a_database_with_employees_changes_neither_schema_nor_data()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        await SeedLegacyEmployeeAsync(database);
        var schemaBefore = await SchemaAsync(database);
        var dataBefore = await DataAsync(database);
        Assert.Single(dataBefore);

        await MigrateToAsync(database, After);

        Assert.Equal(schemaBefore, await SchemaAsync(database));                        // identical columns, indexes and constraints
        Assert.Equal(dataBefore, await DataAsync(database));
        Assert.Contains(After, await AppliedAsync(database));
    }

    [Fact]
    public async Task The_token_is_the_xmin_system_column_and_not_a_stored_column()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        await SeedLegacyEmployeeAsync(database);

        // xmin is readable on every row but is not listed as a user column.
        Assert.Equal("1", (await database.QueryAsync("select count(*) from \"Employees\" where xmin::text::bigint > 0")).Single());
        Assert.Empty(await database.QueryAsync("select column_name from information_schema.columns where table_name = 'Employees' and column_name = 'xmin'"));
    }

    [Fact]
    public async Task Rolling_it_back_and_applying_it_again_is_harmless()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        await SeedLegacyEmployeeAsync(database);
        var schema = await SchemaAsync(database);
        var data = await DataAsync(database);

        await MigrateToAsync(database, Before);
        Assert.DoesNotContain(After, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaAsync(database));
        Assert.Equal(data, await DataAsync(database));

        await MigrateToAsync(database, After);
        Assert.Contains(After, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaAsync(database));
        Assert.Equal(data, await DataAsync(database));
    }
}
