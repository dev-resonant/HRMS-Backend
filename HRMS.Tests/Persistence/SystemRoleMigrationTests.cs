using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Infrastructure.Authorization;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Proves AddSystemRoles against legacy data through the real EF Core migration runner. Each test uses a throwaway
/// database inside the disposable container, migrated to the previous migration, filled with legacy rows through raw SQL,
/// then migrated forward.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SystemRoleMigrationTests
{
    private const string Before = "20261014112003_AddUserEmailNormalizedCheck";
    private const string After = "20261016130045_AddSystemRoles";

    private readonly PostgresFixture _fx;

    public SystemRoleMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private sealed record Legacy(Guid CompanyId, Guid AdminRoleId, Guid AdminUserId, Guid SecondAdminUserId, Guid CustomRoleId, Guid CustomUserId, Guid EmployeeId);

    // A legacy company as the old seeders/tests produced it: a per-company "Admin" role (IsSystem, permissions {}), users on it,
    // one user on a custom company role, and an employee record linked to the admin user.
    private static async Task<Legacy> SeedLegacyCompanyAsync(ScratchDatabase database, string domain)
    {
        var l = new Legacy(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dept = Guid.NewGuid();

        await database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{l.CompanyId}','Co','{domain}','','Asia/Kolkata',true,now());" +
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{l.AdminRoleId}','{l.CompanyId}','Admin','Administrator','{{}}',true,now());" +
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{l.CustomRoleId}','{l.CompanyId}','Custom','Custom','{{\"employees\":[\"view_own\"]}}',false,now());" +
            UserSql(l.AdminUserId, l.CompanyId, l.AdminRoleId, $"admin@{domain}") +
            UserSql(l.SecondAdminUserId, l.CompanyId, l.AdminRoleId, $"second@{domain}") +
            UserSql(l.CustomUserId, l.CompanyId, l.CustomRoleId, $"custom@{domain}") +
            $"insert into \"Departments\"(\"Id\",\"CompanyId\",\"Name\",\"CreatedAt\") values ('{dept}','{l.CompanyId}','Eng',now());");

        await database.ExecuteAsync(
            "insert into \"Employees\"(\"Id\",\"UserId\",\"CompanyId\",\"DepartmentId\",\"EmpCode\",\"Designation\",\"JoiningDate\",\"Status\",\"CreatedAt\") " +
            $"values ('{l.EmployeeId}','{l.AdminUserId}','{l.CompanyId}','{dept}','E1','Admin','2024-01-01',1,now());");

        return l;
    }

    private static string UserSql(Guid id, Guid company, Guid role, string email)
        => "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"IsFirstLogin\",\"CreatedAt\") " +
           $"values ('{id}','{company}','{role}','Legacy','{email}','x',true,false,now());";

    private static Task<List<string>> SystemRoles_(ScratchDatabase d)
        => d.QueryAsync("select \"Name\" from \"Roles\" where \"CompanyId\" is null order by 1");

    private static Task<List<string>> Applied(ScratchDatabase d)
        => d.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\" order by 1");

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
    public async Task Legacy_admin_users_move_to_hr_admin_and_every_company_user_and_employee_id_is_preserved()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var a = await SeedLegacyCompanyAsync(database, "a.test");
        var b = await SeedLegacyCompanyAsync(database, "b.test");
        var companiesBefore = await database.QueryAsync("select \"Id\"::text || \"Domain\" from \"Companies\" order by 1");
        var usersBefore = await database.QueryAsync("select \"Id\"::text || \"CompanyId\"::text || \"Email\" || \"PasswordHash\" || \"IsActive\"::text from \"Users\" order by 1");
        var employeesBefore = await database.QueryAsync("select \"Id\"::text || \"UserId\"::text || \"EmpCode\" from \"Employees\" order by 1");

        await MigrateToAsync(database, After);

        Assert.Contains(After, await Applied(database));
        Assert.Equal(new[] { "employee", "hr_admin", "manager", "super_admin" }, await SystemRoles_(database));

        var hrAdminId = SystemRoles.HrAdminRoleId;
        foreach (var legacy in new[] { a, b })
        {
            Assert.Equal(hrAdminId.ToString(), (await database.QueryAsync($"select \"RoleId\"::text from \"Users\" where \"Id\"='{legacy.AdminUserId}'")).Single());
            Assert.Equal(hrAdminId.ToString(), (await database.QueryAsync($"select \"RoleId\"::text from \"Users\" where \"Id\"='{legacy.SecondAdminUserId}'")).Single());
            Assert.Equal(legacy.CustomRoleId.ToString(), (await database.QueryAsync($"select \"RoleId\"::text from \"Users\" where \"Id\"='{legacy.CustomUserId}'")).Single());   // custom role untouched
        }

        // Nothing else changed: identical ids, emails, hashes, activity, employee links; no row deleted.
        Assert.Equal(companiesBefore, await database.QueryAsync("select \"Id\"::text || \"Domain\" from \"Companies\" order by 1"));
        Assert.Equal(usersBefore, await database.QueryAsync("select \"Id\"::text || \"CompanyId\"::text || \"Email\" || \"PasswordHash\" || \"IsActive\"::text from \"Users\" order by 1"));
        Assert.Equal(employeesBefore, await database.QueryAsync("select \"Id\"::text || \"UserId\"::text || \"EmpCode\" from \"Employees\" order by 1"));
        Assert.Equal("2", (await database.QueryAsync("select count(*) from \"Roles\" where \"Name\"='Admin'")).Single());          // legacy rows kept, now unused
        Assert.Equal("2", (await database.QueryAsync("select count(*) from \"Roles\" where \"Name\"='Custom'")).Single());
    }

    [Fact]
    public async Task System_roles_have_no_company_are_flagged_system_and_match_the_domain_definitions()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);

        await using var db = database.CreateDbContext();
        var roles = await db.Roles.AsNoTracking().Where(r => r.CompanyId == null).ToListAsync();

        Assert.Equal(4, roles.Count);

        foreach (var definition in SystemRoles.Definitions)
        {
            var role = roles.Single(r => r.Name == definition.Key);

            Assert.Equal(definition.Id, role.Id);
            Assert.True(role.IsSystem);
            Assert.Equal(definition.DisplayName, role.DisplayName);
            AssertSamePermissions(definition.Permissions, role.Permissions);
        }
    }

    private static void AssertSamePermissions(IReadOnlyDictionary<string, string[]> expected, string actualJson)
    {
        using var document = JsonDocument.Parse(actualJson);
        var actual = document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(expected, e => Assert.Equal(e.Value.OrderBy(x => x, StringComparer.Ordinal), actual[e.Key]));
    }

    [Fact]
    public async Task Migration_is_idempotent_and_never_overwrites_an_existing_system_role()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);

        // A system role that already exists (for example inserted by a seeder) with permissions an administrator customised.
        var existing = Guid.NewGuid();
        await database.ExecuteAsync(
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{existing}',null,'manager','My Manager','{{\"employees\":[\"view_own\"]}}',true,now());");

        await MigrateToAsync(database, After);

        Assert.Equal(new[] { "employee", "hr_admin", "manager", "super_admin" }, await SystemRoles_(database));
        Assert.Equal(new[] { $"{existing}|My Manager|{{\"employees\": [\"view_own\"]}}" },
            await database.QueryAsync("select \"Id\"::text || '|' || \"DisplayName\" || '|' || \"Permissions\"::text from \"Roles\" where \"Name\"='manager' and \"CompanyId\" is null"));

        // Re-running the seeder (insert-only) changes nothing either.
        await using (var db = database.CreateDbContext())
        {
            await SystemRoleSeeder.EnsureAsync(db, SystemRoleKeys.HrAdmin);
            await SystemRoleSeeder.EnsureAsync(db, SystemRoleKeys.HrAdmin);
        }

        Assert.Equal(new[] { "employee", "hr_admin", "manager", "super_admin" }, await SystemRoles_(database));
    }

    [Fact]
    public async Task The_seeder_creates_all_four_roles_once_on_a_database_that_has_none()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);

        for (var i = 0; i < 3; i++)
        {
            await using var db = database.CreateDbContext();
            var role = await SystemRoleSeeder.EnsureAsync(db, SystemRoleKeys.HrAdmin);
            Assert.Equal(SystemRoles.HrAdminRoleId, role.Id);
        }

        Assert.Equal(new[] { "employee", "hr_admin", "manager", "super_admin" }, await SystemRoles_(database));
        Assert.Equal("4", (await database.QueryAsync("select count(*) from \"Roles\"")).Single());
    }

    [Fact]
    public async Task Migration_aborts_without_changing_anything_when_a_reserved_name_is_used_by_a_non_system_role()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var legacy = await SeedLegacyCompanyAsync(database, "conflict.test");
        await database.ExecuteAsync(
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{Guid.NewGuid()}',null,'employee','Rogue','{{}}',false,now());");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, After));

        Assert.Contains("AddSystemRoles", Messages(failure));
        Assert.Contains("No data was changed", Messages(failure));
        Assert.DoesNotContain(After, await Applied(database));
        Assert.Equal(new[] { "employee" }, await SystemRoles_(database));                                                // nothing inserted
        Assert.Equal(legacy.AdminRoleId.ToString(), (await database.QueryAsync($"select \"RoleId\"::text from \"Users\" where \"Id\"='{legacy.AdminUserId}'")).Single());   // nothing repointed
    }

    [Fact]
    public async Task Duplicate_system_role_keys_are_impossible_after_the_migration()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{Guid.NewGuid()}',null,'hr_admin','Dup','{{}}',true,now());"));

        Assert.Contains("IX_Roles_Name_System", Messages(duplicate));
    }

    [Fact]
    public async Task Down_moves_hr_admin_users_back_to_a_company_admin_role_and_removes_the_system_roles()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        var legacy = await SeedLegacyCompanyAsync(database, "down.test");
        await MigrateToAsync(database, After);

        await MigrateToAsync(database, Before);

        Assert.DoesNotContain(After, await Applied(database));
        Assert.Empty(await SystemRoles_(database));
        Assert.Equal(legacy.AdminRoleId.ToString(), (await database.QueryAsync($"select \"RoleId\"::text from \"Users\" where \"Id\"='{legacy.AdminUserId}'")).Single());
        Assert.Equal("3", (await database.QueryAsync("select count(*) from \"Users\"")).Single());
    }

    [Fact]
    public async Task Down_aborts_without_data_loss_while_a_user_still_has_a_non_migrated_system_role()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        var legacy = await SeedLegacyCompanyAsync(database, "down2.test");
        await database.ExecuteAsync($"update \"Users\" set \"RoleId\"='{SystemRoles.ManagerRoleId}' where \"Id\"='{legacy.CustomUserId}'");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, Before));

        Assert.Contains("still use a system role", Messages(failure));
        Assert.Contains(After, await Applied(database));
        Assert.Equal(new[] { "employee", "hr_admin", "manager", "super_admin" }, await SystemRoles_(database));
        Assert.Equal("3", (await database.QueryAsync("select count(*) from \"Users\"")).Single());
    }
}
