using System.Reflection;
using System.Text.Json;
using HRMS.API.Authorization;
using HRMS.API.Controllers;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Infrastructure.Authorization;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace HRMS.Tests.Authorization;

public class PermissionCatalogTests
{
    [Fact]
    public void The_catalog_is_exactly_the_approved_D3_list()
    {
        Assert.Equal(new[] { "view_own", "view_team", "view_all", "create", "edit_own", "edit_any", "deactivate" }, PermissionCatalog.All["employees"]);
        Assert.Equal(new[] { "view_own_department", "view_all", "manage" }, PermissionCatalog.All["departments"]);
        Assert.Equal(new[] { "view", "manage_permissions" }, PermissionCatalog.All["roles"]);
        Assert.Equal(new[] { "configure_company" }, PermissionCatalog.All["system"]);
        Assert.Equal(4, PermissionCatalog.All.Count);
    }

    [Theory]
    [InlineData("employees", "view_own", true)]
    [InlineData("employees", "fly", false)]
    [InlineData("nonsense", "view_own", false)]
    [InlineData("departments", "view_own", false)]   // exists for employees only
    [InlineData(null, "view_own", false)]
    [InlineData("employees", null, false)]
    public void Only_catalog_permissions_are_valid(string? module, string? action, bool expected)
        => Assert.Equal(expected, PermissionCatalog.IsValid(module, action));

    [Fact]
    public void Every_permission_granted_to_a_system_role_is_in_the_catalog()
    {
        foreach (var role in SystemRoles.Definitions)
        {
            foreach (var (module, actions) in role.Permissions)
            {
                foreach (var action in actions)
                {
                    Assert.True(PermissionCatalog.IsValid(module, action), $"{role.Key}: {module}.{action}");
                }
            }
        }
    }

    [Fact]
    public void The_four_system_roles_exist_and_each_inherits_the_one_below()
    {
        Assert.Equal(SystemRoleKeys.All, SystemRoles.Definitions.Select(d => d.Key));
        Assert.Equal(4, SystemRoles.Definitions.Select(d => d.Id).Distinct().Count());

        var ordered = SystemRoles.Definitions.ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            foreach (var (module, actions) in ordered[i - 1].Permissions)
            {
                Assert.True(ordered[i].Permissions.TryGetValue(module, out var higher), $"{ordered[i].Key} lacks module {module}");
                Assert.True(actions.All(higher!.Contains), $"{ordered[i].Key} lacks something {ordered[i - 1].Key} has in {module}");
            }
        }
    }

    [Fact]
    public void An_employee_has_no_list_permission_and_a_manager_has_team_but_not_company_scope()
    {
        var employee = SystemRoles.Find("employee")!.Permissions["employees"];
        var manager = SystemRoles.Find("manager")!.Permissions["employees"];

        Assert.DoesNotContain("view_team", employee);
        Assert.DoesNotContain("view_all", employee);
        Assert.Contains("view_team", manager);
        Assert.DoesNotContain("view_all", manager);
        Assert.DoesNotContain("edit_any", manager);
    }

    [Fact]
    public void System_role_json_round_trips_through_the_strict_parser()
    {
        foreach (var role in SystemRoles.Definitions)
        {
            var parsed = RolePermissions.Parse(role.PermissionsJson);

            Assert.Equal(role.Permissions.Keys.OrderBy(k => k), parsed.Keys.OrderBy(k => k));
            Assert.All(role.Permissions, p => Assert.Equal(p.Value.OrderBy(a => a), parsed[p.Key].OrderBy(a => a)));
        }
    }
}

public class RolePermissionsParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"employees\"")]
    [InlineData("{\"employees\":\"view_own\"}")]
    [InlineData("{\"employees\":{\"view_own\":true}}")]
    [InlineData("{\"employees\":[]}")]
    [InlineData("{\"employees\":[\"fly\"]}")]
    [InlineData("{\"nonsense\":[\"view_own\"]}")]
    [InlineData("{\"employees\":[1,null,true,[\"view_own\"]]}")]
    public void Anything_that_is_not_a_valid_grant_parses_to_nothing(string? json)
        => Assert.Empty(RolePermissions.Parse(json));

    [Fact]
    public void Valid_entries_survive_next_to_invalid_ones()
    {
        var parsed = RolePermissions.Parse("{\"employees\":[\"view_own\",\"fly\",7,\"view_own\"],\"x\":[\"y\"],\"roles\":\"view\"}");

        Assert.Equal(new[] { "employees" }, parsed.Keys);
        Assert.Equal(new[] { "view_own" }, parsed["employees"]);
    }

    [Fact]
    public void Matching_is_case_sensitive()
        => Assert.Empty(RolePermissions.Parse("{\"Employees\":[\"View_Own\"]}"));
}

public class CurrentUserFailClosedTests
{
    private static CurrentUserService Unloaded() => new(null!, new TenantContext());

    [Fact]
    public void An_unloaded_current_user_is_not_resolved_and_every_property_throws()
    {
        var user = Unloaded();

        Assert.False(user.IsResolved);
        Assert.Throws<UnauthorizedAccessException>(() => user.UserId);
        Assert.Throws<UnauthorizedAccessException>(() => user.CompanyId);
        Assert.Throws<UnauthorizedAccessException>(() => user.RoleKey);
        Assert.Throws<UnauthorizedAccessException>(() => user.EmployeeId);
        Assert.Throws<UnauthorizedAccessException>(() => user.Permissions);
        Assert.Throws<UnauthorizedAccessException>(() => user.HasPermission("employees", "view_own"));
    }

    [Fact]
    public async Task Loading_without_a_resolved_tenant_returns_NotFound_and_never_touches_the_database()
    {
        var user = Unloaded();                                               // a null DbContext would throw if it were used

        Assert.Equal(CurrentUserLoadResult.NotFound, await user.LoadAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(user.IsResolved);
    }

    [Fact]
    public async Task Loading_with_a_token_company_that_differs_from_the_tenant_returns_NotFound()
    {
        var tenant = new TenantContext();
        tenant.SetTenant(Guid.NewGuid(), "a.test");
        var user = new CurrentUserService(null!, tenant);

        Assert.Equal(CurrentUserLoadResult.NotFound, await user.LoadAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(CurrentUserLoadResult.NotFound, await user.LoadAsync(Guid.Empty, tenant.CompanyId));
        Assert.False(user.IsResolved);
    }
}

/// <summary>Deny-by-default convention: every controller action in the production API must declare how it is protected.</summary>
public class ControllerAuthorizationConventionTests
{
    // The only actions allowed to use [SelfService] (no catalog permission). Adding to this list is a deliberate review point.
    private static readonly HashSet<string> SelfServiceAllowList = new()
    {
        $"{nameof(AuthController)}.{nameof(AuthController.Logout)}",
        $"{nameof(AuthController)}.{nameof(AuthController.Me)}",
        $"{nameof(AuthController)}.{nameof(AuthController.ChangePassword)}"       // added with first-login enforcement: acts only on the caller own account
    };

    internal static IEnumerable<MethodInfo> Actions(Assembly assembly)
        => assembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>(true).Any());

    // Class-level and action-level attributes combined, interpreted by the same rule the runtime filter uses.
    internal static DeclarationKind Classify(MethodInfo action)
        => AuthorizationDeclaration.Classify(
            action.GetCustomAttributes(true).Concat(action.DeclaringType!.GetCustomAttributes(true)));

    internal static bool IsDeclared(MethodInfo action) => Classify(action) is DeclarationKind.Anonymous or DeclarationKind.Protected;

    private static string Name(MethodInfo a) => $"{a.DeclaringType!.Name}.{a.Name}";

    [Fact]
    public void Every_production_action_is_anonymous_permission_protected_or_explicitly_self_service()
    {
        var actions = Actions(typeof(AuthController).Assembly).ToList();
        Assert.NotEmpty(actions);

        var undeclared = actions.Where(a => Classify(a) == DeclarationKind.Undeclared).Select(Name).ToList();

        Assert.True(undeclared.Count == 0, "Actions without an authorization declaration: " + string.Join(", ", undeclared));
    }

    [Fact]
    public void No_production_action_has_conflicting_authorization_declarations()
    {
        var conflicts = Actions(typeof(AuthController).Assembly).Where(a => Classify(a) == DeclarationKind.Conflict).Select(Name).ToList();

        Assert.True(conflicts.Count == 0,
            "Actions with contradictory declarations (for example [AllowAnonymous] next to [RequirePermission]/[SelfService]): " + string.Join(", ", conflicts));
    }

    [Fact]
    public void The_intended_anonymous_and_self_service_endpoints_are_classified_as_such()
    {
        var kinds = Actions(typeof(AuthController).Assembly).ToDictionary(Name, Classify);

        Assert.Equal(DeclarationKind.Anonymous, kinds["AuthController.Login"]);
        Assert.Equal(DeclarationKind.Anonymous, kinds["HealthController.Get"]);
        Assert.Equal(DeclarationKind.Protected, kinds["AuthController.Logout"]);
        Assert.Equal(DeclarationKind.Protected, kinds["AuthController.Me"]);
    }

    [Fact]
    public void The_convention_check_rejects_every_known_conflicting_combination()
    {
        // Controllers in the test assembly reproduce the vulnerable shapes; the convention must flag each of them.
        var kinds = Actions(typeof(AuthzProbeController).Assembly).ToDictionary(Name, Classify);

        Assert.Equal(DeclarationKind.Conflict, kinds["AuthzAnonymousClassProbeController.Permission"]);        // class [AllowAnonymous] + action [RequirePermission]
        Assert.Equal(DeclarationKind.Conflict, kinds["AuthzAnonymousClassProbeController.Self"]);              // class [AllowAnonymous] + action [SelfService]
        Assert.Equal(DeclarationKind.Conflict, kinds["AuthzPermissionClassProbeController.Anonymous"]);        // class [RequirePermission] + action [AllowAnonymous]
        Assert.Equal(DeclarationKind.Conflict, kinds["AuthzConflictProbeController.PermissionAndSelf"]);
        Assert.Equal(DeclarationKind.Conflict, kinds["AuthzConflictProbeController.BareAuthorizeAndAnonymous"]);

        // ...and must not flag the legitimate shapes next to them.
        Assert.Equal(DeclarationKind.Anonymous, kinds["AuthzAnonymousClassProbeController.Plain"]);
        Assert.Equal(DeclarationKind.Protected, kinds["AuthzPermissionClassProbeController.Normal"]);
        Assert.Equal(DeclarationKind.Protected, kinds["AuthzProbeController.List"]);
        Assert.Equal(DeclarationKind.Undeclared, kinds["AuthzProbeController.Undeclared"]);
    }

    [Fact]
    public void A_bare_authorize_attribute_is_not_enough()
    {
        // [Authorize] alone authenticates but declares no permission; the filter would deny it, and so does the convention.
        var bare = typeof(ProbeControllers).GetMethod(nameof(ProbeControllers.OnlyAuthorize))!;

        Assert.False(IsDeclared(bare));
    }

    [Fact]
    public void The_convention_check_detects_an_undeclared_action()
    {
        var actions = Actions(typeof(AuthzProbeController).Assembly).Where(a => a.DeclaringType == typeof(AuthzProbeController)).ToList();

        Assert.Contains(actions, a => a.Name == nameof(AuthzProbeController.Undeclared) && !IsDeclared(a));
        Assert.Contains(actions, a => a.Name == nameof(AuthzProbeController.List) && IsDeclared(a));
    }

    [Fact]
    public void Only_the_allow_listed_actions_use_SelfService()
    {
        var used = Actions(typeof(AuthController).Assembly)
            .Where(a => a.GetCustomAttributes<SelfServiceAttribute>().Any())
            .Select(a => $"{a.DeclaringType!.Name}.{a.Name}")
            .ToHashSet();

        Assert.Equal(SelfServiceAllowList, used);
    }

    [Fact]
    public void Every_RequirePermission_in_the_production_API_names_a_catalog_permission()
    {
        var required = Actions(typeof(AuthController).Assembly)
            .SelectMany(a => a.GetCustomAttributes<RequirePermissionAttribute>()
                .Concat(a.DeclaringType!.GetCustomAttributes<RequirePermissionAttribute>(true)))    // action- and controller-level
            .ToList();

        Assert.All(required, r => Assert.True(PermissionCatalog.IsValid(r.Module, r.Action), $"{r.Module}.{r.Action}"));
    }

    // The global registration itself is proven at runtime by An_action_that_declares_nothing_is_denied_for_an_authenticated_caller.
}

[NonController]
public class ProbeControllers : ControllerBase
{
    [HttpGet("x")]
    [Authorize]
    public IActionResult OnlyAuthorize() => Ok();
}
