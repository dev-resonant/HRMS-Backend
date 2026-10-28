using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using HRMS.Infrastructure.Roles;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HRMS.Tests.Roles;

/// <summary>
/// Optimistic concurrency of roles (Role.Version = PostgreSQL xmin) through the real RoleRepository on real PostgreSQL (guarded
/// Testcontainers). Tests that could touch the shared built-in role rows run in scratch databases.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RoleConcurrencyTests
{
    private readonly PostgresFixture _fx;

    public RoleConcurrencyTests(PostgresFixture fx) => _fx = fx;

    private sealed class FakeUser : ICurrentUser
    {
        public FakeUser(Guid companyId) => CompanyId = companyId;

        public bool IsResolved => true;
        public Guid UserId => Guid.NewGuid();
        public Guid CompanyId { get; }
        public string RoleKey => "test";
        public string RoleDisplayName => "Test";
        public Guid? EmployeeId => null;
        public bool PasswordChangeRequired => false;
        public bool IsPlatform => false;
        public IReadOnlyDictionary<string, string[]> Permissions => new Dictionary<string, string[]>();
        public bool HasPermission(string module, string action) => false;
    }

    private sealed class UnresolvedUser : ICurrentUser
    {
        public bool IsResolved => false;
        public Guid UserId => throw new UnauthorizedAccessException();
        public Guid CompanyId => throw new UnauthorizedAccessException();
        public string RoleKey => throw new UnauthorizedAccessException();
        public string RoleDisplayName => throw new UnauthorizedAccessException();
        public Guid? EmployeeId => throw new UnauthorizedAccessException();
        public bool PasswordChangeRequired => throw new UnauthorizedAccessException();
        public bool IsPlatform => throw new UnauthorizedAccessException();
        public IReadOnlyDictionary<string, string[]> Permissions => throw new UnauthorizedAccessException();
        public bool HasPermission(string module, string action) => throw new UnauthorizedAccessException();
    }

    private static RoleRepository Repo(HrmsDbContext db, ICurrentUser user) => new(db, user, NullLogger<RoleRepository>.Instance);

    private static async Task<string> StoredAsync(Func<HrmsDbContext> create, Guid roleId)
    {
        await using var db = create();
        return await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.Name + "|" + r.DisplayName + "|" + r.Permissions).SingleAsync();
    }

    private async Task<(Guid CompanyId, Guid RoleId)> NewCompanyRoleAsync(string permissions = "{\"employees\":[\"view_own\"]}")
    {
        var company = await AuthzTestData.CreateCompanyAsync(_fx);
        var roleId = await AuthzTestData.CreateCompanyRoleAsync(_fx, company.CompanyId, permissions, "Editable");
        return (company.CompanyId, roleId);
    }

    // ================= normal update =================

    [Fact]
    public async Task A_normal_update_succeeds_and_moves_the_version()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        var canonical = RolePermissions.ToJson(new Dictionary<string, string[]> { ["employees"] = new[] { "view_own", "view_team" } });

        await using var db = _fx.CreateDbContext();
        var repo = Repo(db, new FakeUser(companyId));
        var role = await repo.GetForUpdateAsync(roleId);
        Assert.NotNull(role);
        var versionBefore = role!.Version;
        Assert.NotEqual(0u, versionBefore);                                                  // the token is really read from xmin

        role.Permissions = canonical;
        await repo.SaveChangesAsync();

        await using var verify = _fx.CreateDbContext();
        var stored = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == roleId);
        Assert.Equal(canonical, RolePermissions.ToJson(RolePermissions.Parse(stored.Permissions)));
        Assert.NotEqual(versionBefore, stored.Version);
    }

    [Fact]
    public async Task Saving_without_a_change_writes_nothing_and_keeps_the_version()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();

        await using var db = _fx.CreateDbContext();
        var repo = Repo(db, new FakeUser(companyId));
        var role = (await repo.GetForUpdateAsync(roleId))!;
        var version = role.Version;

        await repo.SaveChangesAsync();

        await using var verify = _fx.CreateDbContext();
        Assert.Equal(version, (await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == roleId)).Version);
    }

    // ================= stale updates =================

    [Fact]
    public async Task The_second_of_two_updates_from_the_same_version_is_a_concurrency_conflict_and_changes_nothing()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        await using var first = _fx.CreateDbContext();
        await using var second = _fx.CreateDbContext();
        var firstRepo = Repo(first, new FakeUser(companyId));
        var secondRepo = Repo(second, new FakeUser(companyId));
        var firstRole = (await firstRepo.GetForUpdateAsync(roleId))!;
        var secondRole = (await secondRepo.GetForUpdateAsync(roleId))!;
        Assert.Equal(firstRole.Version, secondRole.Version);                                // both editors read the same version

        firstRole.Permissions = "{\"employees\":[\"view_own\",\"view_team\"]}";
        await firstRepo.SaveChangesAsync();
        var committed = await StoredAsync(_fx.CreateDbContext, roleId);

        secondRole.Permissions = "{\"departments\":[\"manage\"]}";
        var failure = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => secondRepo.SaveChangesAsync());

        Assert.IsAssignableFrom<ConflictException>(failure);                                 // still a 409 for the existing error mapping
        Assert.Contains("changed by someone else", failure.Message);
        Assert.Equal(committed, await StoredAsync(_fx.CreateDbContext, roleId));             // the first editor's state is intact
        Assert.DoesNotContain("departments", await StoredAsync(_fx.CreateDbContext, roleId));
    }

    [Fact]
    public async Task After_reloading_the_stale_editor_can_save()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        await using var first = _fx.CreateDbContext();
        await using var second = _fx.CreateDbContext();
        var firstRole = (await Repo(first, new FakeUser(companyId)).GetForUpdateAsync(roleId))!;
        var secondRole = (await Repo(second, new FakeUser(companyId)).GetForUpdateAsync(roleId))!;
        firstRole.Permissions = "{\"employees\":[\"view_team\"]}";
        await Repo(first, new FakeUser(companyId)).SaveChangesAsync();
        secondRole.Permissions = "{\"employees\":[\"view_all\"]}";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Repo(second, new FakeUser(companyId)).SaveChangesAsync());

        await using var retry = _fx.CreateDbContext();                                      // a new request: read again, then write
        var repo = Repo(retry, new FakeUser(companyId));
        var fresh = (await repo.GetForUpdateAsync(roleId))!;
        fresh.Permissions = "{\"employees\":[\"view_all\"]}";
        await repo.SaveChangesAsync();

        Assert.Contains("view_all", await StoredAsync(_fx.CreateDbContext, roleId));
    }

    [Fact]
    public async Task Many_simultaneous_editors_of_the_same_version_produce_exactly_one_winner()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        var contexts = new List<HrmsDbContext>();
        var roles = new List<Role>();
        for (var i = 0; i < 8; i++)
        {
            var db = _fx.CreateDbContext();
            contexts.Add(db);
            roles.Add((await Repo(db, new FakeUser(companyId)).GetForUpdateAsync(roleId))!);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = roles.Select((role, i) => Task.Run(async () =>
        {
            role.Permissions = RolePermissions.ToJson(new Dictionary<string, string[]> { ["employees"] = new[] { i % 2 == 0 ? "view_team" : "view_all" }, ["departments"] = new[] { i < 4 ? "manage" : "view_all" } });
            await gate.Task;
            try
            {
                await Repo(contexts[i], new FakeUser(companyId)).SaveChangesAsync();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })).ToArray();
        gate.SetResult();
        var outcomes = await Task.WhenAll(saves);
        foreach (var db in contexts) await db.DisposeAsync();

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ConcurrencyConflictException>(o));
        var stored = RolePermissions.Parse((await StoredAsync(_fx.CreateDbContext, roleId)).Split('|')[2]);
        var winner = Array.FindIndex(outcomes, o => o is null);
        Assert.Equal(roles[winner].Permissions, RolePermissions.ToJson(stored));             // the stored state is exactly one editor's, not a mixture
    }

    [Fact]
    public async Task A_role_deleted_meanwhile_is_reported_as_a_conflict_not_a_generic_failure()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        await using var db = _fx.CreateDbContext();
        var repo = Repo(db, new FakeUser(companyId));
        var role = (await repo.GetForUpdateAsync(roleId))!;
        await using (var other = _fx.CreateDbContext())
        {
            await other.Roles.Where(r => r.Id == roleId).ExecuteDeleteAsync();
        }

        role.Permissions = "{\"employees\":[\"view_team\"]}";

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.SaveChangesAsync());
    }

    // ================= unrelated failures are not conflicts =================

    [Fact]
    public async Task Unrelated_database_failures_surface_unchanged_and_are_not_reported_as_conflicts()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        var companyId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        await database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{companyId}','Co','scratch-{companyId:N}.test','','Asia/Kolkata',true,now());" +
            $"insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values ('{roleId}','{companyId}','Custom','Custom','{{}}',false,now());");

        // 1) an invalid jsonb value (the application never builds one, but the database is the last line of defence)
        await using (var db = database.CreateDbContext())
        {
            var repo = Repo(db, new FakeUser(companyId));
            var role = (await repo.GetForUpdateAsync(roleId))!;
            role.Permissions = "this is not json";

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => repo.SaveChangesAsync());

            Assert.IsNotType<ConcurrencyConflictException>(failure);
            var inner = Assert.IsType<DbUpdateException>(failure);
            Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Assert.IsType<PostgresException>(inner.InnerException).SqlState);
        }

        // 2) a unique-index violation: a built-in role renamed to another built-in role's key
        await using (var db = database.CreateDbContext())
        {
            var employeeRole = await db.Roles.SingleAsync(r => r.Id == SystemRoles.EmployeeRoleId);
            employeeRole.Name = SystemRoleKeys.Manager;
            var repo = Repo(db, new FakeUser(companyId));

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => repo.SaveChangesAsync());

            Assert.IsNotType<ConcurrencyConflictException>(failure);
            var inner = Assert.IsType<DbUpdateException>(failure);
            Assert.Equal("IX_Roles_Name_System", Assert.IsType<PostgresException>(inner.InnerException).ConstraintName);
        }

        // Nothing was changed by either failure.
        await using var verify = database.CreateDbContext();
        Assert.Equal("{}", (await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == roleId)).Permissions);
        Assert.Equal(SystemRoleKeys.Employee, (await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == SystemRoles.EmployeeRoleId)).Name);
    }

    [Fact]
    public async Task A_cancelled_save_is_not_reported_as_a_conflict()
    {
        var (companyId, roleId) = await NewCompanyRoleAsync();
        await using var db = _fx.CreateDbContext();
        var repo = Repo(db, new FakeUser(companyId));
        var role = (await repo.GetForUpdateAsync(roleId))!;
        role.Permissions = "{\"employees\":[\"view_team\"]}";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.SaveChangesAsync(cancelled.Token));

        Assert.IsNotType<ConcurrencyConflictException>(failure);
        Assert.Equal("Editable|Editable|{\"employees\": [\"view_own\"]}", await StoredAsync(_fx.CreateDbContext, roleId));
    }

    // ================= scope of the repository =================

    [Fact]
    public async Task The_repository_only_reaches_built_in_roles_and_roles_of_the_callers_company()
    {
        var (companyId, ownRole) = await NewCompanyRoleAsync();
        var (_, foreignRole) = await NewCompanyRoleAsync();

        await using var db = _fx.CreateDbContext();
        var repo = Repo(db, new FakeUser(companyId));

        Assert.NotNull(await repo.GetForUpdateAsync(ownRole));
        Assert.NotNull(await repo.GetForUpdateAsync(SystemRoles.HrAdminRoleId));
        Assert.Null(await repo.GetForUpdateAsync(foreignRole));                              // another company's role looks like a missing one
        Assert.Null(await repo.GetForUpdateAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Repo(db, new UnresolvedUser()).GetForUpdateAsync(ownRole));   // fails closed
    }

    // ================= the token is the system column =================

    [Fact]
    public async Task The_version_is_the_xmin_system_column_and_no_physical_column_exists()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();

        Assert.Empty(await database.QueryAsync("select column_name from information_schema.columns where table_name = 'Roles' and column_name = 'xmin'"));
        Assert.Equal("4", (await database.QueryAsync("select count(*) from \"Roles\" where xmin::text::bigint > 0")).Single());   // readable on every (system) row
    }

    [Fact]
    public async Task Existing_role_data_is_untouched_by_the_new_token()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var db = database.CreateDbContext();
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        Assert.Equal(SystemRoleKeys.All.OrderBy(k => k), roles.Select(r => r.Name));
        foreach (var definition in SystemRoles.Definitions)
        {
            Assert.Equal(definition.PermissionsJson, RolePermissions.ToJson(RolePermissions.Parse(roles.Single(r => r.Name == definition.Key).Permissions)));
        }
    }
}
