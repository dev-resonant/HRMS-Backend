using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence.SeedData;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Persistence;

/// <summary>
/// One-time Super Admin bootstrap (SuperAdminProvisioner). Every test runs in a fresh scratch database inside the guarded Testcontainers
/// instance, because the rule "nothing is created while any Super Admin exists" must be evaluated on a database that has none.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SuperAdminProvisioningTests
{
    private readonly PostgresFixture _fx;
    private readonly BCryptPasswordHasher _hasher = new();

    public SuperAdminProvisioningTests(PostgresFixture fx) => _fx = fx;

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private sealed record Setup(ScratchDatabase Database, string Domain, string HrEmail, string HrPassword);

    /// <summary>A scratch database with the first company and its HR Admin, created by the real production seeder.</summary>
    private async Task<Setup> NewDatabaseAsync(bool designatePlatform = true)
    {
        var database = await _fx.CreateScratchDatabaseAsync();
        var domain = $"sa-{Guid.NewGuid().ToString("N")[..10]}.test";
        var hrEmail = $"admin@{domain}";
        var hrPassword = TestData.NewPassword();

        await using var db = database.CreateDbContext();
        await ProductionDbSeeder.SeedAsync(db, _hasher, Config(("HRMS_TENANT_DOMAIN", domain), ("HRMS_ADMIN_EMAIL", hrEmail), ("HRMS_ADMIN_PASSWORD", hrPassword)));

        if (designatePlatform)
        {
            // Stands in for the authorized operator's explicit designation (docs/PLATFORM_ADMINISTRATION.md), in a throwaway database only.
            // The application itself never does this.
            await database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Domain\" = '{domain}'");
        }

        return new Setup(database, domain, hrEmail, hrPassword);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static IConfiguration SaConfig(string domain, string email, string password, string? name = null)
        => Config((SuperAdminProvisioner.TenantDomainSetting, domain), (SuperAdminProvisioner.EmailSetting, email),
            (SuperAdminProvisioner.PasswordSetting, password), (SuperAdminProvisioner.NameSetting, name));

    private async Task<SuperAdminProvisioningOutcome> RunAsync(ScratchDatabase database, IConfiguration config, ListLogger? logger = null)
    {
        await using var db = database.CreateDbContext();
        return await SuperAdminProvisioner.ProvisionAsync(db, _hasher, config, logger ?? new ListLogger());
    }

    private static async Task<string> SnapshotAsync(ScratchDatabase database)
    {
        await using var db = database.CreateDbContext();
        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Id + c.Name + c.Domain + c.IsActive).ToListAsync();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id + u.Email + u.PasswordHash + u.RoleId + u.IsActive + u.IsFirstLogin).ToListAsync();
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id + r.Name + r.Permissions).ToListAsync();
        return string.Join("|", companies) + "#" + string.Join("|", users) + "#" + string.Join("|", roles);
    }

    private static async Task<int> SuperAdminCountAsync(ScratchDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.Users.CountAsync(u => u.Role.Name == SystemRoleKeys.SuperAdmin && u.Role.CompanyId == null);
    }

    // ================= creation =================

    [Fact]
    public async Task The_first_run_creates_one_super_admin_with_a_hashed_temporary_password_and_a_security_event()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var password = TestData.NewPassword();
        var email = $"Boss@{setup.Domain}";
        var logger = new ListLogger();
        var before = await SnapshotAsync(setup.Database);

        var outcome = await RunAsync(setup.Database, SaConfig(setup.Domain.ToUpperInvariant(), email, password), logger);

        Assert.Equal(SuperAdminProvisioningOutcome.Created, outcome);
        await using var db = setup.Database.CreateDbContext();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Domain == setup.Domain);
        var user = await db.Users.AsNoTracking().Include(u => u.Role).SingleAsync(u => u.Role.Name == SystemRoleKeys.SuperAdmin);
        Assert.Equal(company.Id, user.CompanyId);
        Assert.Equal(email.ToLowerInvariant(), user.Email);                                 // normalized like every other e-mail
        Assert.Equal("Super Administrator", user.Name);
        Assert.True(user.Role.IsSystem && user.Role.CompanyId is null);
        Assert.True(user.IsActive);
        Assert.True(user.IsFirstLogin);                                                     // the configured password is temporary
        Assert.NotEqual(password, user.PasswordHash);
        Assert.True(_hasher.Verify(password, user.PasswordHash));

        var evt = await db.SecurityEvents.AsNoTracking().SingleAsync();
        Assert.Equal((SuperAdminProvisioner.EventType, SuperAdminProvisioner.EventReason, user.Id, company.Id), (evt.EventType, evt.Reason, evt.UserId, evt.CompanyId));

        // Nothing else changed.
        var after = await SnapshotAsync(setup.Database);
        Assert.Equal(before.Split('#')[0], after.Split('#')[0]);                            // companies
        Assert.Equal(before.Split('#')[2], after.Split('#')[2]);                            // roles (including their permissions)
        Assert.All(before.Split('#')[1].Split('|'), existing => Assert.Contains(existing, after.Split('#')[1]));   // every earlier user unchanged

        // The log names ids but never the password, the hash or the e-mail address.
        var log = string.Join("\n", logger.Messages);
        Assert.Contains("Super Admin provisioned", log);
        Assert.DoesNotContain(password, log);
        Assert.DoesNotContain(user.PasswordHash, log);
        Assert.DoesNotContain(email, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_custom_name_is_used_and_no_company_is_created()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;

        await RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword(), "  Vendor Operator "));

        await using var db = setup.Database.CreateDbContext();
        Assert.Equal("Vendor Operator", (await db.Users.AsNoTracking().SingleAsync(u => u.Role.Name == SystemRoleKeys.SuperAdmin)).Name);
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    // ================= rerun, existing data =================

    [Fact]
    public async Task Running_again_changes_nothing_and_a_different_configuration_is_ignored_with_a_warning()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var config = SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword());
        await RunAsync(setup.Database, config);
        var afterFirst = await SnapshotAsync(setup.Database);

        var again = await RunAsync(setup.Database, config);
        var logger = new ListLogger();
        var other = await RunAsync(setup.Database, SaConfig(setup.Domain, $"second@{setup.Domain}", TestData.NewPassword()), logger);

        Assert.Equal(SuperAdminProvisioningOutcome.AlreadyProvisioned, again);
        Assert.Equal(SuperAdminProvisioningOutcome.ExistingSuperAdminKept, other);
        Assert.Equal(afterFirst, await SnapshotAsync(setup.Database));                      // not one byte changed, hash included
        Assert.Equal(1, await SuperAdminCountAsync(setup.Database));
        Assert.Contains(logger.Messages, m => m.StartsWith("Warning") && m.Contains("different Super Admin already exists"));
        await using var db = setup.Database.CreateDbContext();
        Assert.Equal(1, await db.SecurityEvents.CountAsync());
    }

    [Fact]
    public async Task A_deactivated_super_admin_is_not_resurrected_by_a_leftover_setting()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var config = SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword());
        await RunAsync(setup.Database, config);
        await using (var db = setup.Database.CreateDbContext())
        {
            await db.Users.Where(u => u.Role.Name == SystemRoleKeys.SuperAdmin).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var outcome = await RunAsync(setup.Database, config);

        Assert.Equal(SuperAdminProvisioningOutcome.AlreadyProvisioned, outcome);
        await using var verify = setup.Database.CreateDbContext();
        var admin = await verify.Users.AsNoTracking().SingleAsync(u => u.Role.Name == SystemRoleKeys.SuperAdmin);
        Assert.False(admin.IsActive);                                                       // still deactivated, still the only one
    }

    [Fact]
    public async Task An_existing_HR_admin_with_the_configured_email_is_never_promoted()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var before = await SnapshotAsync(setup.Database);
        var password = TestData.NewPassword();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, setup.HrEmail, password)));

        Assert.Contains("not promoted", failure.Message);
        Assert.Contains("hr_admin", failure.Message);
        Assert.DoesNotContain(password, failure.ToString());
        Assert.DoesNotContain(setup.HrEmail, failure.Message);
        Assert.Equal(before, await SnapshotAsync(setup.Database));                          // role, hash and every other column untouched
        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
        await using var db = setup.Database.CreateDbContext();
        Assert.Equal(0, await db.SecurityEvents.CountAsync());
    }

    // ================= placement: only the designated platform company =================

    private static async Task<List<string>> PlatformFlagsAsync(ScratchDatabase database)
        => await database.QueryAsync("select \"Domain\" || '=' || \"IsPlatform\"::text from \"Companies\" order by 1");

    [Fact]
    public async Task A_super_admin_is_never_created_in_an_ordinary_company_and_nothing_is_designated_automatically()
    {
        var setup = await NewDatabaseAsync(designatePlatform: false);
        await using var _ = setup.Database;
        var password = TestData.NewPassword();
        var before = await SnapshotAsync(setup.Database);
        var flagsBefore = await PlatformFlagsAsync(setup.Database);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", password)));

        Assert.Contains("not the designated platform company", failure.Message);
        Assert.Contains("never designated automatically", failure.Message);
        Assert.DoesNotContain(password, failure.ToString());
        Assert.Equal(before, await SnapshotAsync(setup.Database));                          // no user, no role, no company changed
        Assert.Equal(flagsBefore, await PlatformFlagsAsync(setup.Database));                // and the company was not designated as a side effect
        Assert.Equal(new[] { $"{setup.Domain}=false" }, flagsBefore);
        await using var db = setup.Database.CreateDbContext();
        Assert.Equal(0, await db.SecurityEvents.CountAsync());
        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
    }

    [Fact]
    public async Task Naming_an_ordinary_company_is_refused_even_when_a_different_company_is_the_platform()
    {
        var setup = await NewDatabaseAsync();                                                // the seeded company IS the platform
        await using var _ = setup.Database;
        var customer = $"customer-{Guid.NewGuid().ToString("N")[..8]}.test";
        await setup.Database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"CreatedAt\") values ('{Guid.NewGuid()}','Customer','{customer}','','Asia/Kolkata',true,now())");
        var before = await SnapshotAsync(setup.Database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(customer, $"boss@{customer}", TestData.NewPassword())));

        Assert.Equal(before, await SnapshotAsync(setup.Database));
        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
    }

    [Fact]
    public async Task Provisioning_in_the_platform_company_works_and_leaves_the_designation_exactly_as_it_was()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var flagsBefore = await PlatformFlagsAsync(setup.Database);

        Assert.Equal(SuperAdminProvisioningOutcome.Created, await RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword())));

        Assert.Equal(flagsBefore, await PlatformFlagsAsync(setup.Database));
        Assert.Equal(new[] { $"{setup.Domain}=true" }, flagsBefore);
    }

    [Fact]
    public async Task Ambiguous_platform_state_fails_closed()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var other = $"second-{Guid.NewGuid().ToString("N")[..8]}.test";
        // Only reachable if the unique index were removed; the provisioner must not trust a state the index is supposed to prevent.
        await setup.Database.ExecuteAsync("drop index \"IX_Companies_IsPlatform_Single\"");
        await setup.Database.ExecuteAsync(
            $"insert into \"Companies\"(\"Id\",\"Name\",\"Domain\",\"LogoUrl\",\"Timezone\",\"IsActive\",\"IsPlatform\",\"CreatedAt\") values ('{Guid.NewGuid()}','Second','{other}','','Asia/Kolkata',true,true,now())");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword())));

        Assert.Contains("not the designated platform company", failure.Message);
        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
    }

    [Fact]
    public async Task An_already_existing_super_admin_still_makes_a_rerun_a_harmless_no_op_even_if_the_settings_name_an_ordinary_company()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        await RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword()));
        await setup.Database.ExecuteAsync($"update \"Companies\" set \"IsPlatform\" = false where \"Domain\" = '{setup.Domain}'");   // e.g. the designation was later removed
        var before = await SnapshotAsync(setup.Database);

        var outcome = await RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword()));

        Assert.Equal(SuperAdminProvisioningOutcome.AlreadyProvisioned, outcome);            // a restart never bricks the application, and creates nothing
        Assert.Equal(before, await SnapshotAsync(setup.Database));
    }

    // ================= configuration =================

    [Fact]
    public async Task Without_any_setting_the_feature_is_off_and_nothing_is_read_or_written()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var before = await SnapshotAsync(setup.Database);

        var none = await RunAsync(setup.Database, Config());
        var blank = await RunAsync(setup.Database, Config((SuperAdminProvisioner.EmailSetting, "  "), (SuperAdminProvisioner.PasswordSetting, ""), (SuperAdminProvisioner.TenantDomainSetting, null)));

        Assert.Equal(SuperAdminProvisioningOutcome.NotConfigured, none);
        Assert.Equal(SuperAdminProvisioningOutcome.NotConfigured, blank);
        Assert.Equal(before, await SnapshotAsync(setup.Database));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("password")]
    [InlineData("domain")]
    public async Task A_partly_configured_bootstrap_stops_with_a_message_naming_the_missing_setting(string omitted)
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var password = TestData.NewPassword();
        var before = await SnapshotAsync(setup.Database);
        var config = Config(
            (SuperAdminProvisioner.EmailSetting, omitted == "email" ? null : $"boss@{setup.Domain}"),
            (SuperAdminProvisioner.PasswordSetting, omitted == "password" ? null : password),
            (SuperAdminProvisioner.TenantDomainSetting, omitted == "domain" ? null : setup.Domain));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, config));

        var expected = omitted switch { "email" => SuperAdminProvisioner.EmailSetting, "password" => SuperAdminProvisioner.PasswordSetting, _ => SuperAdminProvisioner.TenantDomainSetting };
        Assert.Contains(expected, failure.Message);
        Assert.DoesNotContain(password, failure.ToString());
        Assert.Equal(before, await SnapshotAsync(setup.Database));
    }

    [Theory]
    [InlineData("not-an-email", null, null)]
    [InlineData("a b@x.test", null, null)]
    [InlineData("Name <boss@x.test>", null, null)]
    [InlineData(null, "short-pw1!", null)]                                   // below the password policy
    [InlineData(null, "password with a\ttab inside 123", null)]              // control character
    [InlineData(null, null, "NAME-TOO-LONG")]
    public async Task Invalid_values_are_refused_without_echoing_them_and_nothing_is_created(string? email, string? password, string? name)
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var before = await SnapshotAsync(setup.Database);
        var effectivePassword = password ?? TestData.NewPassword();
        var config = SaConfig(setup.Domain, email ?? $"boss@{setup.Domain}", effectivePassword, name == "NAME-TOO-LONG" ? new string('n', 101) : name);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, config));

        Assert.Contains("Nothing was created", failure.Message);
        Assert.DoesNotContain(effectivePassword, failure.ToString());
        if (email is not null)
        {
            Assert.DoesNotContain(email, failure.ToString());
        }

        Assert.Equal(before, await SnapshotAsync(setup.Database));
    }

    [Fact]
    public async Task A_password_that_is_too_long_for_bcrypt_or_equals_the_email_is_refused()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var email = $"boss-with-a-long-address@{setup.Domain}";

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, email, new string('x', 73))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, email, email.ToUpperInvariant())));

        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
    }

    [Fact]
    public async Task An_unknown_ambiguous_or_inactive_company_is_refused_and_no_company_is_created()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var before = await SnapshotAsync(setup.Database);
        var password = TestData.NewPassword();

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig("nowhere.test", "boss@nowhere.test", password)));
        await setup.Database.ExecuteAsync($"update \"Companies\" set \"IsActive\" = false where \"Domain\" = '{setup.Domain}'");
        var inactive = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setup.Database, SaConfig(setup.Domain, $"boss@{setup.Domain}", password)));

        Assert.Contains("does not match any existing company", unknown.Message);
        Assert.Contains("not active", inactive.Message);
        Assert.DoesNotContain(password, unknown.ToString() + inactive);
        await using var db = setup.Database.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(0, await SuperAdminCountAsync(setup.Database));
        Assert.NotEqual(before, await SnapshotAsync(setup.Database));                       // (only the deliberate IsActive change above)
        Assert.Equal(0, await db.SecurityEvents.CountAsync());
    }

    // ================= concurrency =================

    [Fact]
    public async Task Parallel_starts_create_exactly_one_super_admin()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var config = SaConfig(setup.Domain, $"boss@{setup.Domain}", TestData.NewPassword());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = Enumerable.Range(0, 8).Select(async _ =>
        {
            await gate.Task;
            return await RunAsync(setup.Database, config);
        }).ToArray();
        gate.SetResult();
        var outcomes = await Task.WhenAll(runs);

        Assert.Equal(1, outcomes.Count(o => o == SuperAdminProvisioningOutcome.Created));
        Assert.Equal(7, outcomes.Count(o => o == SuperAdminProvisioningOutcome.AlreadyProvisioned));
        Assert.Equal(1, await SuperAdminCountAsync(setup.Database));
        await using var db = setup.Database.CreateDbContext();
        Assert.Equal(1, await db.SecurityEvents.CountAsync());
    }

    // ================= sign-in, scope and the platform boundary =================

    [Fact]
    public async Task The_super_admin_signs_in_through_the_normal_flow_must_change_the_password_and_stays_inside_its_company()
    {
        var setup = await NewDatabaseAsync();
        await using var _ = setup.Database;
        var password = TestData.NewPassword();
        var email = $"boss@{setup.Domain}";
        await RunAsync(setup.Database, SaConfig(setup.Domain, email, password));

        // A second company with an employee, to prove the Super Admin cannot reach it.
        var otherDomain = $"other-{Guid.NewGuid().ToString("N")[..8]}.test";
        Guid otherDepartment, otherEmployee;
        await using (var db = setup.Database.CreateDbContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Other", Domain = otherDomain, LogoUrl = "", IsActive = true };
            var department = new Department { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Foreign Dept" };
            var user = new User { Id = Guid.NewGuid(), CompanyId = company.Id, RoleId = SystemRoles.Definitions.Single(d => d.Key == SystemRoleKeys.Employee).Id, Name = "Foreign Person", Email = $"p@{otherDomain}", PasswordHash = "x", IsActive = true };
            var employee = new Employee { Id = Guid.NewGuid(), UserId = user.Id, CompanyId = company.Id, DepartmentId = department.Id, EmpCode = "EMP-0001", Designation = "Engineer", JoiningDate = new DateOnly(2024, 1, 1) };
            db.Companies.Add(company);
            db.Departments.Add(department);
            db.Users.Add(user);
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            (otherDepartment, otherEmployee) = (department.Id, employee.Id);
        }

        using var factory = new HrmsWebApplicationFactory(_fx, scratch: setup.Database);
        var client = factory.ClientFor(setup.Domain);

        // A wrong password and a guessable default both fail; the configured one works and demands a change.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync(email, "password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync(email, "Admin@12345678")).StatusCode);
        var login = await client.PostLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal("super_admin", body.GetProperty("user").GetProperty("role").GetString());
        Assert.True(body.GetProperty("user").GetProperty("passwordChangeRequired").GetBoolean());
        Assert.DoesNotContain(password, await login.Content.ReadAsStringAsync());

        // Until the password is changed nothing else works - not even the roles listing.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Get, "/api/roles", token)).StatusCode);

        var change = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password") { Content = JsonContent.Create(new { currentPassword = password, newPassword = TestData.NewPassword() }) };
        change.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(change)).StatusCode);

        var roles = await client.SendAsync(HttpMethod.Get, "/api/roles", token);
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        Assert.Contains("super_admin", await roles.Content.ReadAsStringAsync());

        // Ordinary endpoints stay company-scoped: no foreign employee or department, and the token is useless on the other host.
        var employees = await client.SendAsync(HttpMethod.Get, "/api/employees", token);
        Assert.Equal(HttpStatusCode.OK, employees.StatusCode);
        Assert.DoesNotContain("Foreign Person", await employees.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Get, $"/api/employees/{otherEmployee}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Get, $"/api/departments/{otherDepartment}", token)).StatusCode);
        var foreignHost = factory.ClientFor(otherDomain);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignHost.SendAsync(HttpMethod.Get, "/api/employees", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignHost.SendAsync(HttpMethod.Get, "/api/roles", token)).StatusCode);
    }

    [Fact]
    public void Super_admin_is_the_only_role_with_role_management_and_the_other_roles_are_unchanged()
    {
        var byKey = SystemRoles.Definitions.ToDictionary(d => d.Key, d => d.Permissions);

        Assert.True(byKey[SystemRoleKeys.SuperAdmin].ContainsKey(PermissionNames.Roles.Module));
        Assert.Equal(new[] { PermissionNames.Roles.ManagePermissions, PermissionNames.Roles.View }, byKey[SystemRoleKeys.SuperAdmin][PermissionNames.Roles.Module].OrderBy(a => a));
        Assert.All(new[] { SystemRoleKeys.Employee, SystemRoleKeys.Manager, SystemRoleKeys.HrAdmin }, key => Assert.False(byKey[key].ContainsKey(PermissionNames.Roles.Module), $"{key} must not hold role management"));

        // The super_admin role still contains everything hr_admin has (it inherits the role below).
        foreach (var (module, actions) in byKey[SystemRoleKeys.HrAdmin])
        {
            Assert.True(actions.All(a => byKey[SystemRoleKeys.SuperAdmin][module].Contains(a)));
        }
    }
}

/// <summary>Nothing reachable over HTTP can create or promote a Super Admin.</summary>
[Collection(PostgresCollection.Name)]
public class SuperAdminBoundaryTests : HRMS.Tests.Employees.EmployeeApiTestBase
{
    public SuperAdminBoundaryTests(PostgresFixture fx) : base(fx) { }

    [Fact]
    public async Task HR_cannot_use_the_roles_API_whatever_its_token_claims_and_cannot_create_a_super_admin()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        // A token that CLAIMS super_admin for an HR account: permissions come from the database, so the claim changes nothing.
        var claiming = await AuthzTestData.AddUserAsync(Fx, Factory, company, AuthzTestData.SystemRoleId(SystemRoleKeys.HrAdmin), SystemRoleKeys.SuperAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", hr.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", claiming.Token)).StatusCode);

        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, new
        {
            name = "Sneaky", email = $"sneaky-{Guid.NewGuid():N}@example.test", departmentId = company.DepartmentId, designation = "Engineer",
            joiningDate = "2024-03-01", role = "super_admin", roleKey = "super_admin", roleId = AuthzTestData.SystemRoleId(SystemRoleKeys.SuperAdmin)
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("employee", (await JsonOf(created)).GetProperty("role").GetString());   // the role fields in the body are ignored
    }

    [Fact]
    public void No_API_route_provisions_or_promotes_a_super_admin()
    {
        var routes = typeof(HRMS.API.Controllers.AuthController).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();

        Assert.DoesNotContain(routes, r => r.Contains("Provision", StringComparison.OrdinalIgnoreCase) || r.Contains("SuperAdmin", StringComparison.OrdinalIgnoreCase) || r.Contains("Promote", StringComparison.OrdinalIgnoreCase));
    }
}
