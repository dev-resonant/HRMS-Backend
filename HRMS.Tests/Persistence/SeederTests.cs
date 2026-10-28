using System.Net;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence.SeedData;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Runs the real seeders (including their MigrateAsync call) directly against the disposable container, with throw-away
/// values supplied by the test. Never runs against an existing database.
///
/// The production seeder only bootstraps the FIRST tenant and refuses to create another company when companies already exist
/// (see CompanyDomainTests), so every test that bootstraps a company runs in a fresh, empty scratch database inside the
/// disposable container instead of the shared one, which other tests have already populated.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SeederTests
{
    private readonly PostgresFixture _fx;
    private readonly BCryptPasswordHasher _hasher = new();

    public SeederTests(PostgresFixture fx) => _fx = fx;

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public async Task ProductionDbSeeder_creates_company_admin_role_and_user_and_is_idempotent()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-{suffix}.test";
        var email = $"admin@{domain}";
        var password = TestData.NewPassword();
        var config = Config(("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", email), ("HRMS_ADMIN_PASSWORD", password));

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, config);
            await ProductionDbSeeder.SeedAsync(db, _hasher, config);       // second run must not duplicate anything
        }

        await using var verify = database.CreateDbContext();
        var company = await verify.Companies.AsNoTracking().SingleAsync(c => c.Domain == domain);
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.CompanyId == company.Id);
        var role = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == user.RoleId);

        Assert.True(company.IsActive);
        Assert.Null(role.CompanyId);                                       // the administrator uses the shared hr_admin system role
        Assert.True(role.IsSystem);
        Assert.Equal("hr_admin", role.Name);
        Assert.Equal(0, await verify.Roles.CountAsync(r => r.CompanyId == company.Id));   // no per-company role is created any more
        Assert.Equal(4, await verify.Roles.CountAsync(r => r.CompanyId == null));         // all four system roles, once
        Assert.Equal(email, user.Email);
        Assert.True(user.IsActive);
        Assert.True(_hasher.Verify(password, user.PasswordHash));
    }

    [Fact]
    public async Task A_production_seeded_admin_with_a_lowercase_email_can_log_in_through_the_API()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-login-{suffix}.test";
        var email = $"admin@{domain}";
        var password = TestData.NewPassword();

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, Config(
                ("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", email), ("HRMS_ADMIN_PASSWORD", password)));
        }

        using var factory = new HrmsWebApplicationFactory(_fx, null, database);
        factory.AssertUsesFixtureDatabase();
        var response = await factory.ClientFor(domain).PostLoginAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Audit defect D3: previously skipped. Now enabled - the seeder normalizes the configured email (trim + lowercase).
    [Fact]
    public async Task A_production_seeded_admin_with_a_mixed_case_email_can_log_in()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-case-{suffix}.test";
        var email = $"Admin@{domain}";
        var password = TestData.NewPassword();

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, Config(
                ("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", email), ("HRMS_ADMIN_PASSWORD", password)));
        }

        using var factory = new HrmsWebApplicationFactory(_fx, null, database);
        factory.AssertUsesFixtureDatabase();
        var response = await factory.ClientFor(domain).PostLoginAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_mixed_case_configured_admin_email_is_stored_normalized_and_login_accepts_any_casing_and_surrounding_whitespace()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-norm-{suffix}.test";
        var configured = $"  Admin.User@{domain.ToUpperInvariant()}  ";       // mixed case AND padded, as an operator might type it
        var password = TestData.NewPassword();

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, Config(
                ("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", configured), ("HRMS_ADMIN_PASSWORD", password)));
        }

        // 1) stored according to the policy, 2) the same rule holds for the stored value, 3) the new hash uses work factor 12
        await using (var verify = database.CreateDbContext())
        {
            var company = await verify.Companies.AsNoTracking().SingleAsync(c => c.Domain == domain);
            var user = await verify.Users.AsNoTracking().SingleAsync(u => u.CompanyId == company.Id);
            Assert.Equal($"admin.user@{domain}", user.Email);
            Assert.Equal(HRMS.Domain.Common.EmailNormalizer.Normalize(user.Email), user.Email);
            Assert.StartsWith("$2a$12$", user.PasswordHash);
        }

        // 4) login accepts the email in different casings and with surrounding whitespace
        using var factory = new HrmsWebApplicationFactory(_fx, null, database);
        factory.AssertUsesFixtureDatabase();
        var client = factory.ClientFor(domain);
        foreach (var attempt in new[]
        {
            configured,                                              // exactly as configured (padded, mixed case)
            $"admin.user@{domain}",                                  // canonical
            $"ADMIN.USER@{domain.ToUpperInvariant()}",
            $"Admin.User@{domain}",
            $"\t admin.user@{domain} \n"
        })
        {
            var response = await client.PostLoginAsync(attempt, password);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"login with '{attempt.Replace("\t", "\\t").Replace("\n", "\\n")}' returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Seeding_twice_with_a_mixed_case_email_does_not_create_a_second_account()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-idem-{suffix}.test";
        var config = Config(("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", $"Admin@{domain}"), ("HRMS_ADMIN_PASSWORD", TestData.NewPassword()));

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, config);
            await ProductionDbSeeder.SeedAsync(db, _hasher, config);
        }

        await using var verify = database.CreateDbContext();
        var company = await verify.Companies.AsNoTracking().SingleAsync(c => c.Domain == domain);
        Assert.Equal(1, await verify.Users.CountAsync(u => u.CompanyId == company.Id));
    }

    [Fact]
    public async Task Development_and_production_seeders_apply_the_same_email_rule()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var domain = $"seed-rule-{suffix}.test";

        await using (var db = database.CreateDbContext())
        {
            await ProductionDbSeeder.SeedAsync(db, _hasher, Config(
                ("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", $"Prod.Admin@{domain}"), ("HRMS_ADMIN_PASSWORD", TestData.NewPassword())));
            await DevelopmentDbSeeder.SeedAsync(db, _hasher, Config(("HRMS_DEV_ADMIN_PASSWORD", TestData.NewPassword())));
        }

        await using var verify = database.CreateDbContext();
        var seededByProduction = await verify.Users.AsNoTracking().SingleAsync(u => u.Email == $"prod.admin@{domain}");
        var devCompany = await verify.Companies.AsNoTracking().SingleAsync(c => c.Domain == "localhost");
        var seededByDevelopment = await verify.Users.AsNoTracking().SingleAsync(u => u.CompanyId == devCompany.Id && u.Email == DevelopmentDbSeeder.DevAdminEmail);

        foreach (var user in new[] { seededByProduction, seededByDevelopment })
        {
            Assert.Equal(HRMS.Domain.Common.EmailNormalizer.Normalize(user.Email), user.Email);     // both are fixed points of the one rule
        }

        Assert.Equal("admin@localhost", seededByDevelopment.Email);
    }

    [Theory]
    [InlineData("HRMS_TENANT_DOMAIN")]
    [InlineData("HRMS_ADMIN_EMAIL")]
    [InlineData("HRMS_ADMIN_PASSWORD")]
    public async Task ProductionDbSeeder_refuses_to_run_when_a_required_setting_is_missing(string missing)
    {
        await _fx.VerifyTargetAsync();
        var values = new Dictionary<string, string> {
            ["HRMS_TENANT_DOMAIN"] = $"seed-missing-{Guid.NewGuid():N}.test",
            ["HRMS_ADMIN_EMAIL"] = "admin@example.test",
            ["HRMS_ADMIN_PASSWORD"] = TestData.NewPassword() };
        values.Remove(missing);

        await using var db = _fx.CreateDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionDbSeeder.SeedAsync(db, _hasher, Config(values.Select(v => (v.Key, v.Value)).ToArray())));
    }

    [Fact]
    public async Task DevelopmentDbSeeder_creates_the_localhost_company_and_admin_and_is_idempotent()
    {
        await _fx.VerifyTargetAsync();
        var password = TestData.NewPassword();
        var config = Config(("HRMS_DEV_ADMIN_PASSWORD", password));

        await using (var db = _fx.CreateDbContext())
        {
            await DevelopmentDbSeeder.SeedAsync(db, _hasher, config);
            await DevelopmentDbSeeder.SeedAsync(db, _hasher, config);
        }

        await using var verify = _fx.CreateDbContext();
        var company = await verify.Companies.AsNoTracking().SingleAsync(c => c.Domain == "localhost");
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.CompanyId == company.Id && u.Email == "admin@localhost");

        Assert.True(user.IsActive);
        Assert.True(_hasher.Verify(password, user.PasswordHash));
        var role = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == user.RoleId);
        Assert.Equal("hr_admin", role.Name);
        Assert.Null(role.CompanyId);
        Assert.Equal(0, await verify.Roles.CountAsync(r => r.CompanyId == company.Id));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.CompanyId == null && r.Name == "hr_admin"));
    }
}
