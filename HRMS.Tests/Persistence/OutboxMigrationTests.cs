using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HRMS.Tests.Persistence;

/// <summary>
/// AddOutboxAndSecurityEvents creates two new tables and touches nothing else. Proven through the real EF migration runner on a throwaway
/// database (inside the guarded Testcontainers instance) that already holds an account with a setup token: nothing existing changes, exactly
/// the documented objects appear, the integrity rules hold, and rolling back removes only what the migration added.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxMigrationTests
{
    private const string Before = "20261021130210_AddAccountSetupTokensAndEmployeeContactFields";
    private const string After = "20261021143131_AddOutboxAndSecurityEvents";

    private readonly PostgresFixture _fx;

    public OutboxMigrationTests(PostgresFixture fx) => _fx = fx;

    private static async Task MigrateToAsync(ScratchDatabase database, string target)
    {
        await database.VerifyTargetAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<(Guid Company, Guid User, Guid Token)> SeedLegacyAsync(ScratchDatabase database)
    {
        var company = Guid.NewGuid();
        var department = Guid.NewGuid();
        var user = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var token = Guid.NewGuid();

        await database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{company}','Co','legacy.test','','Asia/Kolkata',true,now());" +
            $"insert into \"Departments\"(\"Id\",\"CompanyId\",\"Name\",\"CreatedAt\") values ('{department}','{company}','Eng',now());" +
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"IsFirstLogin\",\"CreatedAt\") " +
            $"values ('{user}','{company}','5a000000-0000-4000-8000-000000000001','Legacy','legacy@legacy.test','x',true,true,now());" +
            "insert into \"Employees\"(\"Id\",\"UserId\",\"CompanyId\",\"DepartmentId\",\"EmpCode\",\"Designation\",\"JoiningDate\",\"Status\",\"CreatedAt\") " +
            $"values ('{employee}','{user}','{company}','{department}','EMP-0001','Engineer','2024-01-01',1,now());" +
            "insert into \"AccountSetupTokens\"(\"Id\",\"CompanyId\",\"UserId\",\"TokenHash\",\"ExpiresAt\",\"CreatedAt\") " +
            $"values ('{token}','{company}','{user}','{new string('A', 64)}',now() + interval '1 day',now());");

        return (company, user, token);
    }

    private static Task<List<string>> SchemaAsync(ScratchDatabase d) => d.QueryAsync(
        "select 'C:' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') from information_schema.columns where table_schema = 'public' " +
        "union all select 'I:' || indexname || '|' || indexdef from pg_indexes where schemaname = 'public' " +
        "union all select 'K:' || conrelid::regclass::text || '|' || conname || '|' || contype::text from pg_constraint where connamespace = 'public'::regnamespace " +
        "order by 1");

    private static Task<List<string>> DataAsync(ScratchDatabase d) => d.QueryAsync(
        "select u.\"Id\"::text || u.\"Email\" || u.\"IsFirstLogin\"::text || t.\"TokenHash\" || t.\"ExpiresAt\"::text from \"Users\" u join \"AccountSetupTokens\" t on t.\"UserId\" = u.\"Id\" order by 1");

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
    public async Task It_adds_exactly_the_two_tables_and_changes_no_existing_row_or_column()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);
        await SeedLegacyAsync(database);
        var schemaBefore = await SchemaAsync(database);
        var dataBefore = await DataAsync(database);

        await MigrateToAsync(database, After);

        Assert.Equal(dataBefore, await DataAsync(database));                                  // existing rows untouched
        Assert.Empty(schemaBefore.Except(await SchemaAsync(database)));                       // nothing removed or altered
        var added = (await SchemaAsync(database)).Except(schemaBefore).ToList();

        Assert.All(added, a => Assert.True(a.Contains("OutboxMessages") || a.Contains("SecurityEvents"), $"unexpected schema change: {a}"));
        Assert.Contains("C:OutboxMessages.SetupTokenId|uuid|NO|-", added);
        Assert.Contains("C:SecurityEvents.UserId|uuid|YES|-", added);                         // the nullable user is the whole point of the table
        Assert.Contains(added, a => a.StartsWith("I:IX_OutboxMessages_SetupTokenId|CREATE UNIQUE INDEX"));
        Assert.Contains(added, a => a.StartsWith("I:IX_OutboxMessages_Status_NextAttemptAt|"));
        Assert.Contains(added, a => a.Contains("CK_OutboxMessages_Status"));

        // No column anywhere could hold a token, a recipient address or a free-text message.
        Assert.DoesNotContain(added, a => a.StartsWith("C:OutboxMessages.") && (a.Contains(".Token|") || a.Contains(".Email|") || a.Contains(".Payload|") || a.Contains(".Body|")));
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"OutboxMessages\"")).Single());        // no message is invented for old tokens
        Assert.Equal("0", (await database.QueryAsync("select count(*) from \"SecurityEvents\"")).Single());
    }

    [Fact]
    public async Task The_outbox_enforces_one_message_per_token_known_states_and_real_references()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        var (company, user, token) = await SeedLegacyAsync(database);
        string Insert(Guid id, Guid tokenId, Guid owner, string status = "Pending", int attempts = 0) =>
            "insert into \"OutboxMessages\"(\"Id\",\"CompanyId\",\"Kind\",\"UserId\",\"SetupTokenId\",\"Status\",\"Attempts\",\"NextAttemptAt\",\"CreatedAt\") " +
            $"values ('{id}','{company}','account-invitation','{owner}','{tokenId}','{status}',{attempts},now(),now())";

        await database.ExecuteAsync(Insert(Guid.NewGuid(), token, user));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), token, user)));
        var badStatus = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), user, status: "Bogus")));
        var negative = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), user, attempts: -1)));
        var orphanToken = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), user)));
        var protectedToken = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync($"delete from \"AccountSetupTokens\" where \"Id\" = '{token}'"));

        Assert.Contains("IX_OutboxMessages_SetupTokenId", Messages(duplicate));              // the idempotency key
        Assert.Contains("CK_OutboxMessages_Status", Messages(badStatus));
        Assert.Contains("CK_OutboxMessages_Attempts", Messages(negative));
        Assert.Contains("FK_OutboxMessages_AccountSetupTokens_SetupTokenId", Messages(orphanToken));
        Assert.Contains("OutboxMessages", Messages(protectedToken));                           // a token with a pending message cannot silently vanish
    }

    [Fact]
    public async Task Security_events_accept_no_user_but_never_a_user_that_does_not_exist()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, After);
        var (company, user, _) = await SeedLegacyAsync(database);
        string Insert(Guid? owner) =>
            "insert into \"SecurityEvents\"(\"Id\",\"CompanyId\",\"UserId\",\"EventType\",\"Reason\",\"CreatedAt\") " +
            $"values ('{Guid.NewGuid()}','{company}',{(owner is null ? "null" : $"'{owner}'")},'auth.set_password_failed','invalid_token',now())";

        // An account that nothing else references, so the only thing standing in the way of deleting it is its security event.
        var lonely = Guid.NewGuid();
        await database.ExecuteAsync(
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"IsFirstLogin\",\"CreatedAt\") " +
            $"values ('{lonely}','{company}','5a000000-0000-4000-8000-000000000001','Lonely','lonely@legacy.test','x',true,false,now())");

        await database.ExecuteAsync(Insert(null));                                            // anonymous: allowed
        await database.ExecuteAsync(Insert(user));                                            // a real account: allowed
        await database.ExecuteAsync(Insert(lonely));
        var fabricated = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(Insert(Guid.NewGuid())));
        var protectedUser = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync($"delete from \"Users\" where \"Id\" = '{lonely}'"));

        Assert.Contains("FK_SecurityEvents_Users_UserId", Messages(fabricated));
        Assert.Contains("SecurityEvents", Messages(protectedUser));
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

        Assert.Equal(schemaBefore, await SchemaAsync(database));                              // exactly the earlier schema
        Assert.Equal(dataBefore, await DataAsync(database));
        Assert.DoesNotContain(After, await database.QueryAsync("select \"MigrationId\" from \"__EFMigrationsHistory\""));

        await MigrateToAsync(database, After);
        Assert.Equal(dataBefore, await DataAsync(database));
    }

    [Fact]
    public async Task Its_lock_timeout_guard_is_transaction_local_and_leaves_no_session_setting_behind()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await MigrateToAsync(database, Before);

        await MigrateToAsync(database, After);

        Assert.NotEqual("15s", (await database.QueryAsync("show lock_timeout")).Single());     // SET LOCAL ended with the migration's transaction
    }
}
