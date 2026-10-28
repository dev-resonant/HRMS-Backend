using System.Net;
using HRMS.Domain.Common;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence.SeedData;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Company-domain normalization, production-seeder safety and tenant-resolution ambiguity. Seeder tests run in fresh scratch
/// databases inside the disposable container (the seeder only bootstraps the first tenant); HTTP tests use the shared
/// disposable database with unique domains. Raw SQL is used wherever the Company.Domain setter would otherwise normalize a
/// legacy or duplicate value that the tests need to exist.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CompanyDomainTests
{
    private readonly PostgresFixture _fx;
    private readonly BCryptPasswordHasher _hasher = new();

    public CompanyDomainTests(PostgresFixture fx) => _fx = fx;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..10]}.test";

    private async Task SeedAsync(ScratchDatabase database, string domain, string email, string password)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HRMS_TENANT_DOMAIN"] = domain,
            ["HRMS_ADMIN_EMAIL"] = email,
            ["HRMS_ADMIN_PASSWORD"] = password
        }).Build();

        await using var db = database.CreateDbContext();
        await ProductionDbSeeder.SeedAsync(db, _hasher, config);
    }

    /// <summary>Every company, role and user id in the database, for before/after comparison.</summary>
    private static Task<List<string>> IdsAsync(ScratchDatabase database)
        => database.QueryAsync(
            "select 'C:' || \"Id\" from \"Companies\" union all select 'R:' || \"Id\" from \"Roles\" " +
            "union all select 'U:' || \"Id\" from \"Users\" order by 1");

    private static async Task<long> CountAsync(ScratchDatabase database, string table)
        => long.Parse((await database.QueryAsync($"select count(*) from \"{table}\"")).Single());

    private static Task RawCompanyAsync(ScratchDatabase database, Guid id, string domain)
        => database.ExecuteAsync(
            "insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values (@id,'Raw Co',@domain,'','Asia/Kolkata',true,now())",
            ("id", id), ("domain", domain));

    // ---- production seeder: matching ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mixed_case_configured_domain_finds_the_existing_company_and_preserves_every_id()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var domain = Unique("dom-case");
        var password = TestData.NewPassword();
        await SeedAsync(database, domain, $"admin@{domain}", password);
        var before = await IdsAsync(database);

        await SeedAsync(database, domain.ToUpperInvariant(), $"ADMIN@{domain.ToUpperInvariant()}", password);

        Assert.Equal(6, before.Count);                                   // one company, the four system roles, one administrator
        Assert.Equal(before, await IdsAsync(database));                  // identical ids, nothing added
    }

    [Theory]
    [InlineData("  {0}")]
    [InlineData("{0}  ")]
    [InlineData("\t{0}\r\n")]
    [InlineData(" {0}　")]                                      // no-break space and ideographic space, both trimmed by string.Trim()
    [InlineData("  {1}  ")]                                              // padded AND upper-case
    public async Task A_padded_configured_domain_finds_the_existing_company(string pattern)
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var domain = Unique("dom-pad");
        var password = TestData.NewPassword();
        await SeedAsync(database, domain, $"admin@{domain}", password);
        var before = await IdsAsync(database);

        await SeedAsync(database, string.Format(pattern, domain, domain.ToUpperInvariant()), $"admin@{domain}", password);

        Assert.Equal(before, await IdsAsync(database));
    }

    [Fact]
    public async Task Repeated_seeding_with_different_domain_and_email_casing_never_creates_a_second_company_role_or_admin()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var domain = Unique("dom-rep");
        var password = TestData.NewPassword();

        foreach (var (configuredDomain, configuredEmail) in new[]
        {
            (domain, $"admin@{domain}"),
            (domain.ToUpperInvariant(), $"Admin@{domain}"),
            ($" {domain} ", $" ADMIN@{domain.ToUpperInvariant()} "),
            (char.ToUpperInvariant(domain[0]) + domain[1..], $"admin@{domain}"),
            (domain, $"admin@{domain}")
        })
        {
            await SeedAsync(database, configuredDomain, configuredEmail, password);
        }

        Assert.Equal(1, await CountAsync(database, "Companies"));
        Assert.Equal(4, await CountAsync(database, "Roles"));            // the four shared system roles, never one per seeding run
        Assert.Equal(0, long.Parse((await database.QueryAsync("select count(*) from \"Roles\" where \"CompanyId\" is not null")).Single()));
        Assert.Equal(1, await CountAsync(database, "Users"));
    }

    [Fact]
    public async Task A_newly_seeded_domain_is_stored_trimmed_and_lowercase()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var domain = Unique("dom-new");

        await SeedAsync(database, $"  {domain.ToUpperInvariant()} \n", $"admin@{domain}", TestData.NewPassword());

        Assert.Equal(new[] { domain }, await database.QueryAsync("select \"Domain\" from \"Companies\""));
    }

    [Fact]
    public async Task An_existing_company_stored_with_legacy_casing_is_matched_and_its_stored_domain_is_not_rewritten()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        var legacyId = Guid.NewGuid();
        var legacyDomain = "Legacy-Example.TEST";
        await RawCompanyAsync(database, legacyId, legacyDomain);          // as stored before normalization existed

        await SeedAsync(database, "legacy-example.test", "admin@legacy-example.test", TestData.NewPassword());

        Assert.Equal(1, await CountAsync(database, "Companies"));         // no second company
        Assert.Equal(new[] { legacyDomain }, await database.QueryAsync("select \"Domain\" from \"Companies\""));   // value untouched
        Assert.Empty(await database.QueryAsync("select \"CompanyId\"::text from \"Roles\" where \"CompanyId\" is not null"));   // administrator uses the shared hr_admin role
        Assert.Equal(new[] { legacyId.ToString() }, await database.QueryAsync("select \"CompanyId\"::text from \"Users\""));
    }

    // ---- production seeder: safe failure --------------------------------------------------------------------------------

    [Fact]
    public async Task The_seeder_refuses_to_create_another_company_when_none_matches_but_companies_already_exist()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var existing = Unique("dom-first");
        var password = TestData.NewPassword();
        await SeedAsync(database, existing, $"admin@{existing}", password);
        var before = await IdsAsync(database);

        var other = Unique("dom-other");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(database, other, $"admin@{other}", password));

        Assert.Contains("does not match any existing company", failure.Message);
        Assert.DoesNotContain(password, failure.Message);
        Assert.Equal(before, await IdsAsync(database));                   // no company, role or administrator was created
    }

    [Fact]
    public async Task The_seeder_refuses_when_the_configured_domain_matches_more_than_one_company_and_creates_nothing()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        await RawCompanyAsync(database, Guid.NewGuid(), "ambiguous.test");
        await RawCompanyAsync(database, Guid.NewGuid(), "AMBIGUOUS.test");   // allowed by the old case-sensitive unique index

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SeedAsync(database, "Ambiguous.TEST", "admin@ambiguous.test", TestData.NewPassword()));

        Assert.Contains("more than one existing company", failure.Message);
        Assert.Equal(2, await CountAsync(database, "Companies"));
        Assert.Equal(4, await CountAsync(database, "Roles"));             // only the system roles the migration created; the seeder added no role
        Assert.Equal(0, long.Parse((await database.QueryAsync("select count(*) from \"Roles\" where \"CompanyId\" is not null")).Single()));
        Assert.Equal(0, await CountAsync(database, "Users"));
    }

    // ---- development seeder -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_development_seeder_stores_the_domain_through_the_same_normalizer()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            await DevelopmentDbSeeder.SeedAsync(db, _hasher, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["HRMS_DEV_ADMIN_PASSWORD"] = TestData.NewPassword() }).Build());
        }

        var stored = (await database.QueryAsync("select \"Domain\" from \"Companies\"")).Single();
        Assert.Equal(DomainNormalizer.Normalize(DevelopmentDbSeeder.DevDomain), stored);
        Assert.Equal(DomainNormalizer.Normalize(stored), stored);          // a fixed point of the one rule
    }

    [Fact]
    public async Task The_development_seeder_finds_a_company_stored_with_different_casing_instead_of_creating_a_second_one()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.MigrateToLatestAsync();
        await RawCompanyAsync(database, Guid.NewGuid(), "LocalHost");

        await using (var db = database.CreateDbContext())
        {
            await DevelopmentDbSeeder.SeedAsync(db, _hasher, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["HRMS_DEV_ADMIN_PASSWORD"] = TestData.NewPassword() }).Build());
        }

        Assert.Equal(1, await CountAsync(database, "Companies"));
        Assert.Equal(new[] { "LocalHost" }, await database.QueryAsync("select \"Domain\" from \"Companies\""));
    }

    // ---- tenant resolution (over HTTP, shared disposable database) --------------------------------------------------------

    private async Task<(Guid CompanyId, Guid UserId)> RawTenantAsync(string domain, string email, string passwordHash)
    {
        await _fx.VerifyTargetAsync();
        var companyId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values (@c,'Raw Co',@domain,'','Asia/Kolkata',true,now());" +
            "insert into \"Roles\"(\"Id\",\"CompanyId\",\"Name\",\"DisplayName\",\"Permissions\",\"IsSystem\",\"CreatedAt\") values (@r,@c,'Admin','Administrator','{}',true,now());" +
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"CreatedAt\") values (@u,@c,@r,'Raw',@email,@hash,true,now())", connection);
        command.Parameters.AddWithValue("c", companyId);
        command.Parameters.AddWithValue("r", roleId);
        command.Parameters.AddWithValue("u", userId);
        command.Parameters.AddWithValue("domain", domain);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("hash", passwordHash);
        await command.ExecuteNonQueryAsync();

        return (companyId, userId);
    }

    [Fact]
    public async Task A_mixed_case_Host_header_resolves_to_the_intended_company()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        using var factory = new HrmsWebApplicationFactory(_fx);
        factory.AssertUsesFixtureDatabase();

        var response = await factory.ClientFor(tenant.Domain.ToUpperInvariant()).PostLoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_company_stored_with_legacy_upper_case_still_resolves_for_any_host_casing()
    {
        var domain = Unique("legacy-host").ToUpperInvariant();                      // stored upper-case, as before normalization existed
        var email = $"admin@{domain.ToLowerInvariant()}";
        var password = TestData.NewPassword();
        await RawTenantAsync(domain, email, TestData.HashPassword(password));
        using var factory = new HrmsWebApplicationFactory(_fx);
        factory.AssertUsesFixtureDatabase();

        foreach (var host in new[] { domain, domain.ToLowerInvariant() })
        {
            var response = await factory.ClientFor(host).PostLoginAsync(email, password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_host_matching_exactly_one_company_still_resolves_and_other_hosts_still_do_not()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        using var factory = new HrmsWebApplicationFactory(_fx);
        factory.AssertUsesFixtureDatabase();

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor("x" + tenant.Domain).PostLoginAsync(tenant.Email, tenant.Password)).StatusCode);
    }

    [Fact]
    public async Task Domains_that_differ_only_by_case_fail_closed_logging_a_safe_diagnostic_and_nobody_can_log_in()
    {
        var baseDomain = Unique("ambiguous-host");
        var email = $"admin@{baseDomain}";
        var password = TestData.NewPassword();
        var hash = TestData.HashPassword(password);
        // Two tenants whose domains differ only by case, each with an administrator that would accept this email and password:
        // before the fix whichever row PostgreSQL happened to return first would have logged the caller in.
        var first = await RawTenantAsync(baseDomain, email, hash);
        var second = await RawTenantAsync(baseDomain.ToUpperInvariant(), email, hash);

        var logs = new CapturingLoggerProvider();
        using var factory = new HrmsWebApplicationFactory(_fx, services => services.AddSingleton<ILoggerProvider>(logs));
        factory.AssertUsesFixtureDatabase();

        foreach (var host in new[] { baseDomain, baseDomain.ToUpperInvariant() })
        {
            var response = await factory.ClientFor(host).PostLoginAsync(email, password);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);                 // the existing unresolved-tenant behavior
        }

        Assert.Empty(await TestData.GetSessionsAsync(_fx, first.UserId));
        Assert.Empty(await TestData.GetSessionsAsync(_fx, second.UserId));

        var diagnostics = logs.Entries
            .Where(e => e.Level == LogLevel.Error && e.Category.Contains("TenantResolver") && e.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, e =>
        {
            Assert.Contains(first.CompanyId.ToString(), e.Message);                     // identifies the colliding companies
            Assert.Contains(second.CompanyId.ToString(), e.Message);
            Assert.DoesNotContain(password, e.Message);                                 // and exposes no credential or hash
            Assert.DoesNotContain(hash, e.Message);
        });
    }
}
