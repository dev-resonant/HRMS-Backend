using System.Net;
using System.Text.Json;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Tests.Employees;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Roles;

/// <summary>
/// GET /api/roles (read-only). Real API, real PostgreSQL (guarded Testcontainers), real pipeline. Scope under test: the four built-in
/// roles (shared by every company) plus the caller's own company's roles - never another company's.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RolesApiTests : EmployeeApiTestBase
{
    public RolesApiTests(PostgresFixture fx) : base(fx) { }

    private Task<AuthzUser> SuperAdminAsync(AuthzCompany company)
        => AuthzTestData.AddSystemUserAsync(Fx, Factory, company, SystemRoleKeys.SuperAdmin);

    private async Task<Guid> RoleAsync(Guid companyId, string permissionsJson, string name)
        => await AuthzTestData.CreateCompanyRoleAsync(Fx, companyId, permissionsJson, name);

    private async Task<List<JsonElement>> ListAsync(AuthzCompany company, string token, string url = "/api/roles")
    {
        var response = await GetAsync(company, url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).EnumerateArray().ToList();
    }

    private static Dictionary<string, string[]> PermissionsOf(JsonElement role)
    {
        var permissions = role.GetProperty("permissions");
        Assert.Equal(JsonValueKind.Object, permissions.ValueKind);                         // an object, not an escaped JSON string
        return permissions.EnumerateObject().ToDictionary(m => m.Name, m => m.Value.EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    // ================= success and contract =================

    [Fact]
    public async Task A_super_admin_sees_the_four_system_roles_in_hierarchy_order_with_their_exact_permissions()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);

        var roles = (await ListAsync(company, admin.Token)).Where(r => r.GetProperty("scope").GetString() == "system").ToList();

        Assert.Equal(SystemRoleKeys.All, roles.Select(r => r.GetProperty("key").GetString()));
        foreach (var definition in SystemRoles.Definitions)
        {
            var role = roles.Single(r => r.GetProperty("key").GetString() == definition.Key);
            Assert.Equal(definition.Id, role.GetProperty("id").GetGuid());
            Assert.Equal(definition.DisplayName, role.GetProperty("displayName").GetString());
            Assert.True(role.GetProperty("isSystem").GetBoolean());
            Assert.Equal(definition.Permissions.OrderBy(p => p.Key).Select(p => (p.Key, string.Join(",", p.Value.OrderBy(a => a)))),
                PermissionsOf(role).OrderBy(p => p.Key).Select(p => (p.Key, string.Join(",", p.Value.OrderBy(a => a)))));
        }
    }

    [Fact]
    public async Task The_response_contains_exactly_the_documented_fields_and_nothing_from_the_entity()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        await RoleAsync(company.CompanyId, "{\"employees\":[\"view_own\"]}", "Auditor");

        var raw = await (await GetAsync(company, "/api/roles", admin.Token)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);

        Assert.All(document.RootElement.EnumerateArray(), role =>
            Assert.Equal(new[] { "displayName", "id", "isSystem", "key", "permissions", "scope" }, role.EnumerateObject().Select(p => p.Name).OrderBy(n => n)));
        Assert.DoesNotContain(company.CompanyId.ToString(), raw);                           // the company id is not part of the contract
        Assert.DoesNotContain("createdAt", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Own_company_roles_are_listed_after_the_system_roles_with_canonical_permissions()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        var custom = await RoleAsync(company.CompanyId, "{\"employees\":[\"view_team\",\"view_own\"],\"departments\":[\"view_all\"]}", "Auditor");

        var roles = await ListAsync(company, admin.Token);

        Assert.Equal(5, roles.Count);
        var last = roles[^1];
        Assert.Equal(custom, last.GetProperty("id").GetGuid());
        Assert.Equal("Auditor", last.GetProperty("key").GetString());
        Assert.Equal("company", last.GetProperty("scope").GetString());
        Assert.False(last.GetProperty("isSystem").GetBoolean());
        var permissions = PermissionsOf(last);
        Assert.Equal(new[] { "departments", "employees" }, permissions.Keys.OrderBy(k => k));
        Assert.Equal(new[] { "view_own", "view_team" }, permissions["employees"]);        // canonical (sorted) order whatever the stored order
        Assert.Equal(new[] { "view_all" }, permissions["departments"]);
    }

    // ================= tenant isolation =================

    [Fact]
    public async Task Another_companys_custom_roles_are_never_listed_and_the_system_roles_are_shared()
    {
        var companyA = await NewCompanyAsync();
        var companyB = await NewCompanyAsync();
        var adminA = await SuperAdminAsync(companyA);
        var adminB = await SuperAdminAsync(companyB);
        var roleA = await RoleAsync(companyA.CompanyId, "{\"employees\":[\"view_own\"]}", "OnlyInA");
        var roleB = await RoleAsync(companyB.CompanyId, "{\"employees\":[\"view_own\"]}", "OnlyInB");

        var rawA = await (await GetAsync(companyA, "/api/roles", adminA.Token)).Content.ReadAsStringAsync();
        var rawB = await (await GetAsync(companyB, "/api/roles", adminB.Token)).Content.ReadAsStringAsync();

        Assert.Contains("OnlyInA", rawA);
        Assert.DoesNotContain("OnlyInB", rawA);
        Assert.DoesNotContain(roleB.ToString(), rawA);
        Assert.Contains("OnlyInB", rawB);
        Assert.DoesNotContain("OnlyInA", rawB);
        Assert.DoesNotContain(roleA.ToString(), rawB);

        // The shared system roles are the very same rows for both companies.
        static IEnumerable<Guid> SystemIds(string raw) => JsonDocument.Parse(raw).RootElement.EnumerateArray()
            .Where(r => r.GetProperty("scope").GetString() == "system").Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(SystemIds(rawA), SystemIds(rawB));
    }

    [Fact]
    public async Task No_caller_supplied_company_identifier_widens_the_result()
    {
        var company = await NewCompanyAsync();
        var other = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        await RoleAsync(other.CompanyId, "{\"employees\":[\"view_own\"]}", "ForeignRole");

        var viaQuery = await (await GetAsync(company, $"/api/roles?companyId={other.CompanyId}&company_id={other.CompanyId}", admin.Token)).Content.ReadAsStringAsync();
        var viaHeaders = await (await CallAsync(company, HttpMethod.Get, "/api/roles", admin.Token, null,
            ("X-Company-Id", other.CompanyId.ToString()), ("X-Tenant", other.Domain))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("ForeignRole", viaQuery);
        Assert.DoesNotContain("ForeignRole", viaHeaders);
        Assert.Equal(5 - 1, JsonDocument.Parse(viaQuery).RootElement.GetArrayLength());     // the four system roles only
    }

    [Fact]
    public async Task A_super_admins_token_does_not_work_on_another_companys_host()
    {
        var company = await NewCompanyAsync();
        var other = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(other, "/api/roles", admin.Token)).StatusCode);
    }

    // ================= authorization =================

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var company = await NewCompanyAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(company, "/api/roles", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(company, "/api/roles", "not-a-token")).StatusCode);
    }

    [Theory]
    [InlineData(SystemRoleKeys.Employee)]
    [InlineData(SystemRoleKeys.Manager)]
    [InlineData(SystemRoleKeys.HrAdmin)]
    public async Task Roles_without_the_view_permission_are_refused(string roleKey)
    {
        var company = await NewCompanyAsync();
        var user = await AuthzTestData.AddSystemUserAsync(Fx, Factory, company, roleKey);

        var response = await GetAsync(company, "/api/roles", user.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("super_admin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_view_permission_matters_not_the_role_name_and_manage_permissions_alone_is_not_enough()
    {
        var company = await NewCompanyAsync();
        var viewer = await AuthzTestData.AddUserAsync(Fx, Factory, company, await RoleAsync(company.CompanyId, "{\"roles\":[\"view\"]}", "Viewer"), "custom");
        var managerOnly = await AuthzTestData.AddUserAsync(Fx, Factory, company, await RoleAsync(company.CompanyId, "{\"roles\":[\"manage_permissions\"]}", "Editor"), "super_admin");
        var empty = await AuthzTestData.AddUserAsync(Fx, Factory, company, await RoleAsync(company.CompanyId, "{}", "Nothing"), "super_admin");   // claims super_admin, holds nothing

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(company, "/api/roles", viewer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", managerOnly.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", empty.Token)).StatusCode);
    }

    [Fact]
    public async Task Missing_or_inconsistent_authorization_context_fails_closed()
    {
        var company = await NewCompanyAsync();
        var other = await NewCompanyAsync();
        var deactivated = await SuperAdminAsync(company);
        var foreignRole = await AuthzTestData.AddUserAsync(Fx, Factory, company, await RoleAsync(other.CompanyId, "{\"roles\":[\"view\"]}", "NotMine"), "custom");
        var pendingPassword = await SuperAdminAsync(company);
        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == deactivated.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            await db.Users.Where(u => u.Id == pendingPassword.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsFirstLogin, true));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", deactivated.Token)).StatusCode);      // deactivated account
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", foreignRole.Token)).StatusCode);      // role of another company
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", pendingPassword.Token)).StatusCode);  // must change the password first
    }

    // ================= stored permissions are parsed strictly =================

    [Fact]
    public async Task Malformed_and_unknown_stored_permissions_grant_nothing_and_are_not_reported()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        var wrongShapes = new Dictionary<string, string>
        {
            ["ArrayRoot"] = "[1,2,3]",
            ["StringValue"] = "{\"employees\":\"view_own\"}",
            ["NumberActions"] = "{\"employees\":[1,2]}",
            ["Scalar"] = "\"hello\"",
            ["Null"] = "null"
        };
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, json) in wrongShapes)
        {
            ids[name] = await RoleAsync(company.CompanyId, json, name);
        }

        var mixed = await RoleAsync(company.CompanyId, "{\"employees\":[\"view_own\",\"fly\"],\"bogus\":[\"x\"],\"departments\":[\"nope\"]}", "Mixed");

        var roles = await ListAsync(company, admin.Token);

        Assert.All(wrongShapes.Keys, name => Assert.Empty(PermissionsOf(roles.Single(r => r.GetProperty("key").GetString() == name))));
        var mixedPermissions = PermissionsOf(roles.Single(r => r.GetProperty("id").GetGuid() == mixed));
        Assert.Equal(new[] { "employees" }, mixedPermissions.Keys);                          // the unknown module and the module with only unknown actions are gone
        Assert.Equal(new[] { "view_own" }, mixedPermissions["employees"]);                   // the unknown action is gone

        // A user holding such a role has no access at all.
        var holder = await AuthzTestData.AddUserAsync(Fx, Factory, company, ids["StringValue"], "custom");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(company, "/api/roles", holder.Token)).StatusCode);
    }

    [Fact]
    public async Task A_company_less_role_that_is_not_a_system_role_is_not_listed()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        var ghost = Guid.NewGuid();
        var name = "ghost-" + ghost.ToString("N")[..8];
        await using (var db = Fx.CreateDbContext())
        {
            db.Roles.Add(new Role { Id = ghost, CompanyId = null, Name = name, DisplayName = "Ghost", Permissions = "{\"roles\":[\"view\"]}", IsSystem = false });
            await db.SaveChangesAsync();
        }

        try
        {
            var raw = await (await GetAsync(company, "/api/roles", admin.Token)).Content.ReadAsStringAsync();

            Assert.DoesNotContain(name, raw);
            Assert.DoesNotContain(ghost.ToString(), raw);
        }
        finally
        {
            await using var db = Fx.CreateDbContext();                                      // the shared database must not keep a company-less non-system role
            await db.Roles.Where(r => r.Id == ghost).ExecuteDeleteAsync();
        }
    }

    // ================= read-only =================

    [Fact]
    public async Task Listing_changes_nothing_and_writes_no_audit_entry()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        await RoleAsync(company.CompanyId, "{\"employees\":[\"view_own\",\"fly\"],\"zzz\":[]}", "Odd");
        string Stored() { using var db = Fx.CreateDbContext(); return string.Join("|", db.Roles.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id + r.Permissions + r.Name + r.DisplayName)); }
        var before = Stored();
        var auditBefore = (await AuditAsync(company.CompanyId)).Count;

        await ListAsync(company, admin.Token);
        await ListAsync(company, admin.Token);

        Assert.Equal(before, Stored());                                                      // malformed values are not "repaired" in the database
        Assert.Contains("fly", Stored());
        Assert.Equal(auditBefore, (await AuditAsync(company.CompanyId)).Count);
    }

    [Fact]
    public async Task Write_routes_do_not_exist_yet()
    {
        var company = await NewCompanyAsync();
        var admin = await SuperAdminAsync(company);
        var role = SystemRoles.Definitions.First().Id;

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Post, "/api/roles"), (HttpMethod.Put, $"/api/roles/{role}"), (HttpMethod.Put, $"/api/roles/{role}/permissions"),
            (HttpMethod.Delete, $"/api/roles/{role}"), (HttpMethod.Put, $"/api/employees/{Guid.NewGuid()}/role")
        })
        {
            var status = (await CallAsync(company, method, url, admin.Token, new { permissions = new { } })).StatusCode;
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {url} answered {(int)status}");
        }
    }

    // ================= contract =================

    [Fact]
    public async Task The_swagger_document_describes_the_route_the_response_dto_and_the_status_codes()
    {
        var company = await NewCompanyAsync();

        using var document = JsonDocument.Parse(await (await GetAsync(company, "/swagger/v1/swagger.json", null)).Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/roles").GetProperty("get");
        var responses = operation.GetProperty("responses");

        Assert.Equal(new[] { "200", "401", "403" }, responses.EnumerateObject().Select(r => r.Name).OrderBy(n => n));
        Assert.Contains("RoleDto", responses.GetProperty("200").GetRawText());
        Assert.Single(root.GetProperty("paths").GetProperty("/api/roles").EnumerateObject());   // GET only

        // The document-wide Bearer requirement applies (the operation does not opt out).
        Assert.True(root.TryGetProperty("security", out var security) && security.GetRawText().Contains("Bearer"));
        Assert.False(operation.TryGetProperty("security", out var own) && own.GetArrayLength() == 0);

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var dto = schemas.GetProperty("RoleDto").GetProperty("properties");
        Assert.Equal(new[] { "displayName", "id", "isSystem", "key", "permissions", "scope" }, dto.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var permissions = dto.GetProperty("permissions");
        Assert.Equal("object", permissions.GetProperty("type").GetString());                  // an object, not a string
        Assert.Equal("array", permissions.GetProperty("additionalProperties").GetProperty("type").GetString());
        Assert.DoesNotContain(schemas.EnumerateObject(), s => s.Name is "Role" or "User" or "Company");
    }
}
