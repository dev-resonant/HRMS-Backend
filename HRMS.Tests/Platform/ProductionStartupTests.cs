using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Platform;

/// <summary>
/// Real Program.cs start-up in the Production environment (seeders, then the Super Admin bootstrap), against throwaway databases inside the
/// guarded container only. The shared test host uses the Testing environment, where neither runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductionStartupTests
{
    private readonly PostgresFixture _fx;

    public ProductionStartupTests(PostgresFixture fx) => _fx = fx;

    private static Dictionary<string, string?> Settings(string domain, string? superAdminPassword, string? superAdminDomain = null) => new()
    {
        ["Cors:AllowedOrigins:0"] = "https://app.example.test",           // required in Production
        ["HRMS_TENANT_DOMAIN"] = domain,
        ["HRMS_ADMIN_EMAIL"] = $"admin@{domain}",
        ["HRMS_ADMIN_PASSWORD"] = TestData.NewPassword(),
        ["HRMS_SUPERADMIN_EMAIL"] = superAdminPassword is null ? null : $"root@{domain}",
        ["HRMS_SUPERADMIN_PASSWORD"] = superAdminPassword,
        ["HRMS_SUPERADMIN_TENANT_DOMAIN"] = superAdminPassword is null ? null : superAdminDomain ?? domain
    };

    private async Task<Exception?> StartAsync(ScratchDatabase database, Dictionary<string, string?> settings)
    {
        using var factory = new HrmsWebApplicationFactory(_fx, scratch: database, settings: settings, environmentName: "Production");

        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task<string> Count(ScratchDatabase d, string sql) => d.QueryAsync(sql).ContinueWith(t => t.Result.Single());

    private static string Domain() => $"prod-{Guid.NewGuid().ToString("N")[..8]}.test";

    [Fact]
    public async Task Production_start_without_super_admin_settings_seeds_the_company_and_creates_no_super_admin()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();

        Assert.Null(await StartAsync(database, Settings(Domain(), null)));

        Assert.Equal("1", await Count(database, "select count(*) from \"Companies\""));
        Assert.Equal("0", await Count(database, "select count(*) from \"Companies\" where \"IsPlatform\""));
        Assert.Equal("0", await Count(database, "select count(*) from \"Users\" u join \"Roles\" r on r.\"Id\" = u.\"RoleId\" where r.\"Name\" = 'super_admin'"));
    }

    [Fact]
    public async Task Super_admin_settings_without_a_designated_platform_company_stop_start_up_and_create_nothing()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var password = TestData.NewPassword();

        var failure = await StartAsync(database, Settings(Domain(), password));

        Assert.NotNull(failure);
        Assert.DoesNotContain(password, failure!.ToString());
        Assert.Equal("0", await Count(database, "select count(*) from \"Users\" u join \"Roles\" r on r.\"Id\" = u.\"RoleId\" where r.\"Name\" = 'super_admin'"));
        Assert.Equal("0", await Count(database, "select count(*) from \"Companies\" where \"IsPlatform\""));       // nothing designated it
    }

    [Fact]
    public async Task After_the_operator_designates_the_company_a_restart_creates_exactly_one_super_admin_and_later_restarts_change_nothing()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var domain = Domain();
        var password = TestData.NewPassword();
        Assert.NotNull(await StartAsync(database, Settings(domain, password)));

        await database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Domain\" = '{domain}'");   // the manual operator step
        Assert.Null(await StartAsync(database, Settings(domain, password)));

        const string superAdmins = "select count(*) from \"Users\" u join \"Roles\" r on r.\"Id\" = u.\"RoleId\" where r.\"Name\" = 'super_admin'";
        Assert.Equal("1", await Count(database, superAdmins));
        Assert.Equal("true", await Count(database, "select \"IsFirstLogin\"::text from \"Users\" where \"Email\" = 'root@" + domain + "'"));
        var hash = await Count(database, "select \"PasswordHash\" from \"Users\" where \"Email\" = 'root@" + domain + "'");

        Assert.Null(await StartAsync(database, Settings(domain, password)));                      // idempotent
        Assert.Null(await StartAsync(database, Settings(domain, null)));                          // settings removed afterwards
        Assert.Equal("1", await Count(database, superAdmins));
        Assert.Equal(hash, await Count(database, "select \"PasswordHash\" from \"Users\" where \"Email\" = 'root@" + domain + "'"));
    }

    [Fact]
    public async Task Partial_super_admin_settings_stop_start_up()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var settings = Settings(Domain(), null);
        settings["HRMS_SUPERADMIN_EMAIL"] = "root@example.test";                                  // email only

        Assert.NotNull(await StartAsync(database, settings));
        Assert.Equal("0", await Count(database, "select count(*) from \"Users\" u join \"Roles\" r on r.\"Id\" = u.\"RoleId\" where r.\"Name\" = 'super_admin'"));
    }
}
