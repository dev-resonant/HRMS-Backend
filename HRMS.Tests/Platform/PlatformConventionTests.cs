using System.Reflection;
using HRMS.API.Authorization;
using HRMS.API.Controllers;
using HRMS.Domain.Authorization;
using HRMS.Tests.Authorization;
using HRMS.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace HRMS.Tests.Platform;

/// <summary>Architecture conventions for platform-only actions. The rule is shared with the runtime filter (AuthorizationDeclaration.Classify).</summary>
public class PlatformConventionTests
{
    private static string Name(MethodInfo a) => $"{a.DeclaringType!.Name}.{a.Name}";

    private static readonly RequirePermissionAttribute Permission = new(PermissionNames.Roles.Module, PermissionNames.Roles.View);

    // ================= the declaration rule =================

    [Fact]
    public void PlatformOnly_next_to_a_permission_is_a_normal_protected_declaration()
    {
        Assert.Equal(DeclarationKind.Protected, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), Permission }));
        Assert.Equal(DeclarationKind.Protected, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), Permission, Permission }));
    }

    [Fact]
    public void PlatformOnly_never_stands_alone_and_never_combines_with_anonymous_or_self_service_access()
    {
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute() }));                           // no permission
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), new AllowAnonymousAttribute() }));
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), new AllowAnonymousAttribute(), Permission }));
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), new SelfServiceAttribute() }));
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new PlatformOnlyAttribute(), new AuthorizeAttribute() }));   // a bare [Authorize] is not a permission
    }

    [Fact]
    public void Without_PlatformOnly_nothing_about_the_existing_rule_changed()
    {
        Assert.Equal(DeclarationKind.Protected, AuthorizationDeclaration.Classify(new object[] { Permission }));
        Assert.Equal(DeclarationKind.Protected, AuthorizationDeclaration.Classify(new object[] { new SelfServiceAttribute() }));
        Assert.Equal(DeclarationKind.Anonymous, AuthorizationDeclaration.Classify(new object[] { new AllowAnonymousAttribute() }));
        Assert.Equal(DeclarationKind.Undeclared, AuthorizationDeclaration.Classify(Array.Empty<object>()));
        Assert.Equal(DeclarationKind.Conflict, AuthorizationDeclaration.Classify(new object[] { new AllowAnonymousAttribute(), Permission }));
    }

    [Fact]
    public void The_attribute_is_not_an_authorization_attribute_so_it_cannot_grant_anything()
    {
        Assert.False(typeof(IAuthorizeData).IsAssignableFrom(typeof(PlatformOnlyAttribute)));
        Assert.False(typeof(IAllowAnonymous).IsAssignableFrom(typeof(PlatformOnlyAttribute)));
        Assert.Equal("/api/platform", PlatformOnlyAttribute.RoutePrefix);
    }

    // ================= the platform area convention =================

    private static string Template(MethodInfo action)
    {
        var routeTemplate = action.DeclaringType!.GetCustomAttributes<RouteAttribute>(true).FirstOrDefault()?.Template ?? "";
        var actionTemplate = action.GetCustomAttributes<HttpMethodAttribute>(true).FirstOrDefault()?.Template ?? "";

        return ("/" + (actionTemplate.StartsWith('/') ? actionTemplate : $"{routeTemplate}/{actionTemplate}")).Replace("//", "/").TrimEnd('/');
    }

    private static bool InPlatformArea(MethodInfo action)
        => Template(action).StartsWith(PlatformOnlyAttribute.RoutePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Violations of the platform conventions among the controller actions of an assembly.</summary>
    private static List<string> Violations(System.Reflection.Assembly assembly, bool requireAreaForPlatformOnly)
    {
        var violations = new List<string>();

        foreach (var action in ControllerAuthorizationConventionTests.Actions(assembly))
        {
            var metadata = action.GetCustomAttributes(true).Concat(action.DeclaringType!.GetCustomAttributes(true)).ToList();
            var platformOnly = metadata.OfType<PlatformOnlyAttribute>().Any();
            var hasPermission = metadata.OfType<RequirePermissionAttribute>().Any();
            var inArea = InPlatformArea(action);

            if (inArea && !platformOnly) violations.Add($"{Name(action)}: in the /api/platform area without [PlatformOnly]");
            if (platformOnly && !hasPermission) violations.Add($"{Name(action)}: [PlatformOnly] without [RequirePermission]");
            if (platformOnly && requireAreaForPlatformOnly && !inArea) violations.Add($"{Name(action)}: [PlatformOnly] outside /api/platform");
            if (platformOnly && ControllerAuthorizationConventionTests.Classify(action) is DeclarationKind.Conflict or DeclarationKind.Anonymous)
                violations.Add($"{Name(action)}: [PlatformOnly] with a conflicting or anonymous declaration");
        }

        return violations;
    }

    [Fact]
    public void Every_production_action_in_the_platform_area_is_gated_and_every_gated_action_is_in_the_area_with_a_permission()
    {
        // Today the production API has no platform endpoints, so this holds trivially; it is the rule every future platform endpoint must meet.
        Assert.Empty(Violations(typeof(AuthController).Assembly, requireAreaForPlatformOnly: true));
    }

    [Fact]
    public void The_convention_detects_each_known_mistake_in_the_probe_controllers()
    {
        var violations = Violations(typeof(PlatformProbeController).Assembly, requireAreaForPlatformOnly: false);

        Assert.Contains(violations, v => v.StartsWith("PlatformForgottenAttributeProbeController.Get") && v.Contains("without [PlatformOnly]"));
        Assert.Contains(violations, v => v.StartsWith("PlatformNoPermissionProbeController.Get") && v.Contains("without [RequirePermission]"));
        Assert.Contains(violations, v => v.StartsWith("PlatformProbeController.Anonymous"));
        Assert.Contains(violations, v => v.StartsWith("PlatformProbeController.SelfService"));
        // ...and none of the correct shapes.
        Assert.DoesNotContain(violations, v => v.StartsWith("PlatformProbeController.Manage") || v.StartsWith("PlatformProbeController.View"));
        Assert.DoesNotContain(violations, v => v.StartsWith("PlatformActionLevelProbeController"));
    }

    [Fact]
    public void The_outside_the_area_rule_flags_a_gated_action_on_an_ordinary_route()
    {
        var violations = Violations(typeof(PlatformProbeController).Assembly, requireAreaForPlatformOnly: true);

        Assert.Contains(violations, v => v.StartsWith("PlatformActionLevelProbeController.Gated") && v.Contains("outside /api/platform"));
    }

    [Fact]
    public void The_existing_authorization_conventions_still_hold_for_the_production_API()
    {
        var actions = ControllerAuthorizationConventionTests.Actions(typeof(AuthController).Assembly).ToList();

        Assert.NotEmpty(actions);
        Assert.DoesNotContain(actions, a => ControllerAuthorizationConventionTests.Classify(a) is DeclarationKind.Undeclared or DeclarationKind.Conflict);
    }
}
