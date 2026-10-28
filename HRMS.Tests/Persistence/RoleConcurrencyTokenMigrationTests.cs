using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HRMS.Tests.Persistence;

/// <summary>
/// RoleConcurrencyToken is a snapshot-only migration (the token is PostgreSQL's built-in xmin system column). Proven through the real EF
/// migration runner on throwaway databases that already hold roles: it changes nothing in the database, and rolling back is harmless.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RoleConcurrencyTokenMigrationTests
{
    private const string Before = "20261021143131_AddOutboxAndSecurityEvents";
    private const string After = "20261023075625_RoleConcurrencyToken";

    private readonly PostgresFixture _fx;

    public RoleConcurrencyTokenMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static Task<List<string>> SchemaAsync(ScratchDatabase d) => d.QueryAsync(
        "select 'C:' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') from information_schema.columns where table_schema = 'public' " +
        "union all select 'I:' || indexname || '|' || indexdef from pg_indexes where schemaname = 'public' " +
        "union all select 'K:' || conrelid::regclass::text || '|' || conname || '|' || contype::text from pg_constraint where connamespace = 'public'::regnamespace " +
        "order by 1");

    private static Task<List<string>> RolesAsync(ScratchDatabase d) => d.QueryAsync(
        "select \"Id\"::text || coalesce(\"CompanyId\"::text, '-') || \"Name\" || \"DisplayName\" || \"Permissions\"::text || \"IsSystem\"::text from \"Roles\" order by 1");

    private static Task<List<string>> AppliedAsync(ScratchDatabase d) => d.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\" order by 1");

    private static async Task AddCompanyRoleAsync(ScratchDatabase d)
    {
        var company = Guid.NewGuid();
        await d.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{company}','Co','legacy-{company:N}.test','','Asia/Kolkata',true,now());" +
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{Guid.NewGuid()}','{company}','Custom','Custom','{{\"employees\":[\"view_own\"]}}',false,now());");
    }

    [Fact]
    public async Task Applying_it_changes_neither_the_schema_nor_any_role()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        await AddCompanyRoleAsync(database);
        var schemaBefore = await SchemaAsync(database);
        var rolesBefore = await RolesAsync(database);
        Assert.Equal(5, rolesBefore.Count);                                                  // four built-in roles and the custom one

        await MigrateToAsync(database, After);

        Assert.Equal(schemaBefore, await SchemaAsync(database));                              // identical columns, indexes and constraints
        Assert.Equal(rolesBefore, await RolesAsync(database));                                // no permission was touched
        Assert.Contains(After, await AppliedAsync(database));
    }

    [Fact]
    public async Task The_token_is_the_system_column_and_the_migration_is_the_latest_after_it()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);

        Assert.Empty(await database.QueryAsync("select column_name from information_schema.columns where table_name = 'Roles' and column_name = 'xmin'"));
        Assert.Equal("4", (await database.QueryAsync("select count(*) from \"Roles\" where xmin::text::bigint > 0")).Single());
    }

    [Fact]
    public async Task Rolling_it_back_and_applying_it_again_is_harmless()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        await AddCompanyRoleAsync(database);
        var schema = await SchemaAsync(database);
        var roles = await RolesAsync(database);

        await MigrateToAsync(database, Before);
        Assert.DoesNotContain(After, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaAsync(database));
        Assert.Equal(roles, await RolesAsync(database));

        await MigrateToAsync(database, After);
        Assert.Contains(After, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaAsync(database));
        Assert.Equal(roles, await RolesAsync(database));
    }
}
