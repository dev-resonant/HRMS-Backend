using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence.SeedData;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HRMS.Tests.Platform;

/// <summary>
/// Companies.IsPlatform: the database rules (default false, at most one true) and the migration that adds it. Throwaway databases inside the
/// guarded Testcontainers instance only; nothing here designates a company anywhere else.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlatformDesignationTests
{
    private const string Before = "20261023075625_RoleConcurrencyToken";
    private const string After = "20261023105109_AddCompanyIsPlatform";

    private readonly PostgresFixture _fx;

    public PlatformDesignationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static string InsertCompany(Guid id, string domain, string? isPlatform = null)
        => $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\"{(isPlatform is null ? "" : ",\"IsPlatform\"")}) " +
           $"values ('{id}','Co','{domain}','','Asia/Kolkata',true,now(){(isPlatform is null ? "" : "," + isPlatform)})";

    private static Task<List<string>> SchemaAsync(ScratchDatabase d) => d.QueryAsync(
        "select 'C:' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') from information_schema.columns where table_schema = 'public' " +
        "union all select 'I:' || indexname || '|' || indexdef from pg_indexes where schemaname = 'public' " +
        "union all select 'K:' || conrelid::regclass::text || '|' || conname || '|' || contype::text from pg_constraint where connamespace = 'public'::regnamespace " +
        "order by 1");

    private static Task<List<string>> CompaniesAsync(ScratchDatabase d) => d.QueryAsync(
        "select \"Id\"::text || \"Name\" || \"Domain\" || coalesce(\"LogoUrl\", '-') || \"Timezone\" || \"IsActive\"::text || \"CreatedAt\"::text from \"Companies\" order by 1");

    // ================= the column and the single-platform rule =================

    [Fact]
    public async Task Companies_default_to_not_platform_and_no_company_is_designated_on_a_fresh_database()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();

        await database.ExecuteAsync(InsertCompany(Guid.NewGuid(), "one.test"));                  // the column is not mentioned at all
        await database.ExecuteAsync(InsertCompany(Guid.NewGuid(), "two.test", "false"));

        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());
        Assert.Equal("2", (await database.QueryAsync("select count(*) from \"Companies\" where not \"IsPlatform\"")).Single());
        Assert.Equal("false", (await database.QueryAsync("select column_default from information_schema.columns where table_name = 'Companies' and column_name = 'IsPlatform'")).Single());
        Assert.Equal("NO", (await database.QueryAsync("select is_nullable from information_schema.columns where table_name = 'Companies' and column_name = 'IsPlatform'")).Single());
    }

    [Fact]
    public async Task Exactly_one_company_can_be_designated_and_a_second_is_refused_by_the_database()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await database.ExecuteAsync(InsertCompany(first, "first.test", "true"));
        await database.ExecuteAsync(InsertCompany(second, "second.test"));
        await database.ExecuteAsync(InsertCompany(Guid.NewGuid(), "third.test"));                // any number of ordinary companies is fine

        var insert = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(InsertCompany(Guid.NewGuid(), "fourth.test", "true")));
        var promote = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Id\" = '{second}'"));

        Assert.Contains("IX_Companies_IsPlatform_Single", insert.ToString());
        Assert.Contains("IX_Companies_IsPlatform_Single", promote.ToString());
        Assert.Equal("1", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());

        // The designation can move: clear the old one first (a deliberate operator step), then set the new one.
        await database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = false where \"Id\" = '{first}'");
        await database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Id\" = '{second}'");
        Assert.Equal(second.ToString(), (await database.QueryAsync("select \"Id\"::text from \"Companies\" where \"IsPlatform\"")).Single());
    }

    [Fact]
    public async Task Concurrent_designations_cannot_both_succeed()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) await database.ExecuteAsync(InsertCompany(id, $"c-{id:N}.test"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = ids.Select(async id =>
        {
            await gate.Task;
            try { await database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Id\" = '{id}'"); return true; }
            catch { return false; }
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal("1", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());
    }

    [Fact]
    public async Task Start_up_seeding_never_designates_a_platform_company()
    {
        await using var production = await _fx.CreateScratchDatabaseAsync();
        await using var development = await _fx.CreateScratchDatabaseAsync();
        var hasher = new BCryptPasswordHasher();
        var domain = $"seed-{Guid.NewGuid().ToString("N")[..8]}.test";

        await using (var db = production.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, hasher, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HRMS_TENANT_DOMAIN"] = domain, ["HRMS_ADMIN_EMAIL"] = $"admin@{domain}", ["HRMS_ADMIN_PASSWORD"] = TestData.NewPassword()
            }).Build());
        }

        await using (var db = development.CreateDbContext())
        {
            await DevelopmentDbSeeder.SeedAsync(db, hasher, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HRMS_DEV_ADMIN_PASSWORD"] = TestData.NewPassword()
            }).Build());
        }

        foreach (var database in new[] { production, development })
        {
            Assert.Equal("1", (await database.QueryAsync("select count(*) from \"Companies\"")).Single());
            Assert.Equal("0", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());
        }
    }

    // ================= the migration =================

    [Fact]
    public async Task Applying_it_designates_nobody_and_leaves_every_company_and_all_other_schema_unchanged()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < ids.Length; i++) await database.ExecuteAsync(InsertCompany(ids[i], $"legacy{i}.test"));
        var schemaBefore = await SchemaAsync(database);
        var companiesBefore = await CompaniesAsync(database);

        await MigrateToAsync(database, After);

        Assert.Equal(companiesBefore, await CompaniesAsync(database));                           // ids, names, domains, timestamps: byte for byte
        Assert.Equal("3", (await database.QueryAsync("select count(*) from \"Companies\" where not \"IsPlatform\"")).Single());
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());

        var after = await SchemaAsync(database);
        Assert.Empty(schemaBefore.Except(after));                                                // nothing removed or altered
        var added = after.Except(schemaBefore).ToList();
        Assert.Equal(2, added.Count);                                                            // exactly the column and the index
        Assert.Contains("C:Companies.IsPlatform|boolean|NO|false", added);
        Assert.Contains(added, a => a.StartsWith("I:IX_Companies_IsPlatform_Single|CREATE UNIQUE INDEX") && a.Contains("WHERE"));
        Assert.Empty(await database.QueryAsync("select column_name from information_schema.columns where table_name = 'Companies' and column_name = 'xmin'"));   // no physical xmin
    }

    [Fact]
    public async Task Rolling_it_back_removes_exactly_what_it_added_and_applying_it_again_works()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        await database.ExecuteAsync(InsertCompany(Guid.NewGuid(), "legacy.test"));
        var schemaBefore = await SchemaAsync(database);
        var companiesBefore = await CompaniesAsync(database);
        await MigrateToAsync(database, After);
        await database.ExecuteAsync("update \"Companies\" set \"IsPlatform\" = true");           // an operator's later designation

        await MigrateToAsync(database, Before);

        Assert.Equal(schemaBefore, await SchemaAsync(database));                                 // the exact earlier schema
        Assert.Equal(companiesBefore, await CompaniesAsync(database));
        Assert.DoesNotContain(After, await database.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\""));

        await MigrateToAsync(database, After);
        Assert.Equal(companiesBefore, await CompaniesAsync(database));
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"Companies\" where \"IsPlatform\"")).Single());    // re-applying designates nobody either
    }

    [Fact]
    public async Task The_lock_timeout_guard_does_not_outlive_the_migration()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);

        await MigrateToAsync(database, After);

        Assert.NotEqual("15s", (await database.QueryAsync("show lock_timeout")).Single());
    }
}
