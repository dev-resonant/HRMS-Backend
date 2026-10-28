using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace HRMS.Tests.Platform;

/// <summary>
/// The platform gate ([PlatformOnly] + the database-backed IsPlatform context) through the real pipeline, with test-only probe controllers.
/// Every test runs in its own scratch database inside the guarded container: the single-platform-company rule means the shared test
/// database must never be designated.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlatformGateTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private ScratchDatabase _db = null!;
    private HrmsWebApplicationFactory _factory = null!;

    public PlatformGateTests(PostgresFixture fx) => _fx = fx;

    private sealed record Co(Guid Id, string Domain, Guid DepartmentId);

    private sealed record Who(Guid UserId, Guid EmployeeId, Co Company, string Email, string Token);

    private Co Platform = null!;
    private Co Ordinary = null!;

    public async Task InitializeAsync()
    {
        _db = await _fx.CreateScratchDatabaseAsync();
        await _db.MigrateToLatestAsync();
        Platform = await AddCompanyAsync(isPlatform: true);
        Ordinary = await AddCompanyAsync(isPlatform: false);
        _factory = new HrmsWebApplicationFactory(_fx, ProbeHost.AddProbeController, scratch: _db);
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _db.DisposeAsync();
    }

    // ---------- setup helpers ----------

    private async Task<Co> AddCompanyAsync(bool isPlatform)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var company = new Company { Id = Guid.NewGuid(), Name = $"Co {suffix}", Domain = $"gate-{suffix}.test", LogoUrl = "", IsActive = true, IsPlatform = isPlatform };
        var department = new Department { Id = Guid.NewGuid(), CompanyId = company.Id, Name = $"Dept {suffix}" };

        await using var db = _db.CreateDbContext();
        db.Companies.Add(company);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        return new Co(company.Id, company.Domain, department.Id);
    }

    private async Task<Guid> CustomRoleAsync(Co company, string permissionsJson)
    {
        await using var db = _db.CreateDbContext();
        var role = new Role { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "custom-" + Guid.NewGuid().ToString("N")[..6], DisplayName = "Custom", Permissions = permissionsJson, IsSystem = false };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Who> UserAsync(Co company, string roleKey, string? claimedRole = null, bool active = true, bool firstLogin = false, Guid? roleId = null, IEnumerable<Claim>? extraClaims = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var user = new User
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, RoleId = roleId ?? AuthzTestData.SystemRoleId(roleKey), Name = $"User {suffix}",
            Email = $"u-{suffix}@{company.Domain}", PasswordHash = "not-a-real-hash", IsActive = active, IsFirstLogin = firstLogin
        };
        var employee = new Employee
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = company.Id, DepartmentId = company.DepartmentId, EmpCode = "E" + suffix,
            Designation = "Engineer", JoiningDate = new DateOnly(2024, 1, 1)
        };
        var token = Mint(user.Id, company.Id, user.Email, claimedRole ?? roleKey, extraClaims);

        await using var db = _db.CreateDbContext();
        db.Users.Add(user);
        db.Employees.Add(employee);
        db.Sessions.Add(new Session { UserId = user.Id, TokenHash = TestData.HashToken(token), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();

        return new Who(user.Id, employee.Id, company, user.Email, token);
    }

    private string Mint(Guid userId, Guid companyId, string email, string role, IEnumerable<Claim>? extra)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()), new(JwtRegisteredClaimNames.Email, email),
            new("companyId", companyId.ToString()), new(ClaimTypes.Role, role)
        };
        claims.AddRange(extra ?? Array.Empty<Claim>());

        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_fx.JwtSecret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(_factory.Issuer, _factory.Audience, claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: credentials));
    }

    private Task<HttpResponseMessage> GetAsync(Co host, string url, string? token, params (string Name, string Value)[] headers)
    {
        var client = _factory.ClientFor(host.Domain);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task SqlAsync(string sql) => _db.ExecuteAsync(sql);

    private const string Manage = "/api/platform/probe/manage";
    private const string View = "/api/platform/probe/view";

    // ================= who gets through =================

    [Fact]
    public async Task A_super_admin_of_the_platform_company_passes_the_gate_and_the_context_says_so()
    {
        var admin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);

        var response = await GetAsync(Platform, Manage, admin.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("isPlatform").GetBoolean());
        Assert.Equal(Platform.Id, body.GetProperty("companyId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, View, admin.Token)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_unauthenticated_requests_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(Platform, Manage, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(Platform, Manage, "not-a-token")).StatusCode);
    }

    // ================= who is refused =================

    [Theory]
    [InlineData(SystemRoleKeys.Employee)]
    [InlineData(SystemRoleKeys.Manager)]
    [InlineData(SystemRoleKeys.HrAdmin)]
    public async Task Lower_roles_are_refused_even_inside_the_platform_company(string roleKey)
    {
        var user = await UserAsync(Platform, roleKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, user.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, View, user.Token)).StatusCode);
    }

    [Theory]
    [InlineData(SystemRoleKeys.Employee)]
    [InlineData(SystemRoleKeys.Manager)]
    [InlineData(SystemRoleKeys.HrAdmin)]
    [InlineData(SystemRoleKeys.SuperAdmin)]
    public async Task Every_role_in_an_ordinary_company_is_refused_including_a_super_admin_who_holds_the_permission(string roleKey)
    {
        var user = await UserAsync(Ordinary, roleKey);

        var response = await GetAsync(Ordinary, Manage, user.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("platform", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);   // the answer reveals nothing about a platform
    }

    [Fact]
    public async Task Both_conditions_are_needed_the_platform_company_and_the_permission_for_that_very_action()
    {
        var none = await UserAsync(Platform, "custom", roleId: await CustomRoleAsync(Platform, "{}"));
        var viewOnly = await UserAsync(Platform, "custom", roleId: await CustomRoleAsync(Platform, "{\"roles\":[\"view\"]}"));
        var manageOnly = await UserAsync(Platform, "custom", roleId: await CustomRoleAsync(Platform, "{\"roles\":[\"manage_permissions\"]}"));
        var outsideWithBoth = await UserAsync(Ordinary, "custom", roleId: await CustomRoleAsync(Ordinary, "{\"roles\":[\"view\",\"manage_permissions\"]}"));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, none.Token)).StatusCode);              // platform company, no permission
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, viewOnly.Token)).StatusCode);          // wrong permission
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, View, viewOnly.Token)).StatusCode);                   // right permission for the other action
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, Manage, manageOnly.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, Manage, outsideWithBoth.Token)).StatusCode);   // permissions, wrong company
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, View, outsideWithBoth.Token)).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_and_an_account_that_must_still_change_its_password_are_refused()
    {
        var deactivated = await UserAsync(Platform, SystemRoleKeys.SuperAdmin, active: false);
        var pending = await UserAsync(Platform, SystemRoleKeys.SuperAdmin, firstLogin: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, deactivated.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, pending.Token)).StatusCode);
    }

    // ================= nothing the caller supplies can grant it =================

    [Fact]
    public async Task A_role_name_or_forged_claims_in_the_token_grant_nothing()
    {
        var hrOrdinary = await UserAsync(Ordinary, SystemRoleKeys.HrAdmin, claimedRole: SystemRoleKeys.SuperAdmin,
            extraClaims: new[] { new Claim("platform", "true"), new Claim("isPlatform", "true"), new Claim("scope", "platform") });
        var hrPlatform = await UserAsync(Platform, SystemRoleKeys.HrAdmin, claimedRole: SystemRoleKeys.SuperAdmin,
            extraClaims: new[] { new Claim("platform", "true"), new Claim("permissions", "{\"roles\":[\"manage_permissions\"]}") });

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, Manage, hrOrdinary.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, hrPlatform.Token)).StatusCode);        // right company, wrong (database) permissions
    }

    [Fact]
    public async Task A_token_that_names_the_platform_company_for_a_user_of_an_ordinary_company_is_refused()
    {
        var ordinarySuperAdmin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        var forged = Mint(ordinarySuperAdmin.UserId, Platform.Id, ordinarySuperAdmin.Email, SystemRoleKeys.SuperAdmin, null);
        await using (var db = _db.CreateDbContext())
        {
            db.Sessions.Add(new Session { UserId = ordinarySuperAdmin.UserId, TokenHash = TestData.HashToken(forged), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }

        var onPlatformHost = await GetAsync(Platform, Manage, forged);
        var onOwnHost = await GetAsync(Ordinary, Manage, forged);

        Assert.True(onPlatformHost.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);   // the user does not exist in the company the token claims
        Assert.Equal(HttpStatusCode.Forbidden, onOwnHost.StatusCode);                                         // tenant mismatch
    }

    [Fact]
    public async Task Company_ids_headers_and_query_values_supplied_by_the_caller_change_nothing()
    {
        var ordinaryAdmin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        var platformAdmin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var spoof = new (string, string)[] { ("X-Company-Id", Platform.Id.ToString()), ("X-Tenant", Platform.Domain), ("X-Platform", "true"), ("X-Forwarded-Host", Platform.Domain) };

        foreach (var url in new[] { Manage, $"{Manage}?companyId={Platform.Id}&isPlatform=true&platform=true", $"{Manage}?company_id={Platform.Id}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, url, ordinaryAdmin.Token, spoof)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, url, platformAdmin.Token, spoof)).StatusCode);
        }
    }

    // ================= designation state =================

    [Fact]
    public async Task Without_a_designated_platform_company_everyone_is_refused()
    {
        var admin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var ordinary = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, Manage, admin.Token)).StatusCode);

        await SqlAsync("update \"Companies\" set \"IsPlatform\" = false");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, admin.Token)).StatusCode);          // same token, next request: the change applies at once
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, Manage, ordinary.Token)).StatusCode);
    }

    [Fact]
    public async Task Designating_a_company_takes_effect_on_the_next_request_for_its_users_only()
    {
        await SqlAsync("update \"Companies\" set \"IsPlatform\" = false");
        var admin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, Manage, admin.Token)).StatusCode);

        await SqlAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Id\" = '{Ordinary.Id}'");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Ordinary, Manage, admin.Token)).StatusCode);
    }

    [Fact]
    public async Task Ambiguous_designation_fails_closed_for_everyone()
    {
        var admin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var other = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        // Only reachable if the unique index were removed; the gate must not trust a state the index is meant to prevent.
        await SqlAsync("drop index \"IX_Companies_IsPlatform_Single\"");
        await SqlAsync($"update \"Companies\" set \"IsPlatform\" = true where \"Id\" = '{Ordinary.Id}'");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Platform, Manage, admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, Manage, other.Token)).StatusCode);
    }

    // ================= declarations cannot bypass the gate =================

    [Fact]
    public async Task Action_level_attributes_cannot_switch_the_gate_off()
    {
        var platformAdmin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var ordinaryAdmin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);

        foreach (var url in new[] { "/api/platform/probe/anonymous", "/api/platform/probe/self-service", "/api/platform/probe-nopermission" })
        {
            foreach (var (host, token) in new (Co, string?)[] { (Platform, platformAdmin.Token), (Ordinary, ordinaryAdmin.Token), (Platform, null) })
            {
                var response = await GetAsync(host, url, token);

                if (token is not null)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);                                      // contradictory declaration: denied for every authenticated caller
                }
                else
                {
                    // Without a token ASP.NET's own authorization middleware may answer 401 first (any [Authorize]-derived attribute); never 200.
                    Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
                }

                Assert.DoesNotContain("reached", await response.Content.ReadAsStringAsync());
            }
        }
    }

    [Fact]
    public async Task A_forgotten_attribute_in_the_platform_area_is_refused_at_runtime_for_everyone()
    {
        var platformAdmin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);

        var response = await GetAsync(Platform, "/api/platform/probe-forgotten", platformAdmin.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);                                                  // it carries a permission the admin holds, and still no
        Assert.DoesNotContain("reached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_attribute_works_at_action_level_and_leaves_its_neighbours_alone()
    {
        var platformAdmin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var ordinaryAdmin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Platform, "/api/probe-platform-action/gated", platformAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, "/api/probe-platform-action/gated", ordinaryAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Ordinary, "/api/probe-platform-action/ordinary", ordinaryAdmin.Token)).StatusCode);   // not gated: ordinary rules
    }

    // ================= existing tenant behaviour is unchanged =================

    [Fact]
    public async Task A_platform_administrator_still_sees_only_its_own_companys_employees_and_departments()
    {
        var admin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var foreign = await UserAsync(Ordinary, SystemRoleKeys.Employee);

        var employees = await GetAsync(Platform, "/api/employees", admin.Token);
        var ids = (await JsonAsync(employees)).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(HttpStatusCode.OK, employees.StatusCode);
        Assert.Contains(admin.EmployeeId, ids);
        Assert.DoesNotContain(foreign.EmployeeId, ids);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(Platform, $"/api/employees/{foreign.EmployeeId}", admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(Platform, $"/api/departments/{Ordinary.DepartmentId}", admin.Token)).StatusCode);
        var departments = (await JsonAsync(await GetAsync(Platform, "/api/departments", admin.Token))).EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { Platform.DepartmentId }, departments);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, "/api/employees", admin.Token)).StatusCode);     // the token is useless on another company's host
    }

    [Fact]
    public async Task HR_and_the_existing_roles_listing_behave_exactly_as_before()
    {
        var hr = await UserAsync(Ordinary, SystemRoleKeys.HrAdmin);
        var ordinarySuperAdmin = await UserAsync(Ordinary, SystemRoleKeys.SuperAdmin);
        var platformSuperAdmin = await UserAsync(Platform, SystemRoleKeys.SuperAdmin);
        var foreignEmployee = await UserAsync(Platform, SystemRoleKeys.Employee);

        var ids = (await JsonAsync(await GetAsync(Ordinary, "/api/employees", hr.Token))).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(hr.EmployeeId, ids);
        Assert.DoesNotContain(foreignEmployee.EmployeeId, ids);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Ordinary, "/api/roles", hr.Token)).StatusCode);
        foreach (var (host, user) in new[] { (Ordinary, ordinarySuperAdmin), (Platform, platformSuperAdmin) })
        {
            var roles = await GetAsync(host, "/api/roles", user.Token);                                                   // still company-scoped, not platform-gated
            Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
            Assert.Equal(SystemRoleKeys.All, (await JsonAsync(roles)).EnumerateArray().Select(r => r.GetProperty("key").GetString()).Take(4));
        }
    }
}
