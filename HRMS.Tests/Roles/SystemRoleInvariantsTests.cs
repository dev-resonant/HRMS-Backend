using HRMS.Domain.Authorization;

namespace HRMS.Tests.Roles;

/// <summary>The hierarchy and Super Admin invariants of the built-in roles, derived from the catalog and the seeded definitions.</summary>
public class SystemRoleInvariantsTests
{
    private static Dictionary<string, IReadOnlyDictionary<string, string[]>> Baseline()
        => SystemRoles.Definitions.ToDictionary(d => d.Key, d => (IReadOnlyDictionary<string, string[]>)Clone(d.Permissions));

    private static Dictionary<string, string[]> Clone(IReadOnlyDictionary<string, string[]> source)
        => source.ToDictionary(p => p.Key, p => p.Value.ToArray());

    private static Dictionary<string, string[]> Without(IReadOnlyDictionary<string, string[]> source, string module, string action)
    {
        var copy = Clone(source);
        var remaining = copy[module].Where(a => a != action).ToArray();
        if (remaining.Length == 0) copy.Remove(module); else copy[module] = remaining;
        return copy;
    }

    private static Dictionary<string, string[]> With(IReadOnlyDictionary<string, string[]> source, string module, string action)
    {
        var copy = Clone(source);
        copy[module] = copy.TryGetValue(module, out var existing) ? existing.Append(action).ToArray() : new[] { action };
        return copy;
    }

    private static bool Holds(IReadOnlyDictionary<string, string[]> p, string module, string action) => p.TryGetValue(module, out var a) && a.Contains(action);

    private static string Lower(string key) => SystemRoleKeys.All[SystemRoleKeys.All.ToList().IndexOf(key) - 1];

    private static string Higher(string key) => SystemRoleKeys.All[SystemRoleKeys.All.ToList().IndexOf(key) + 1];

    // ================= the shipped definitions =================

    [Fact]
    public void The_shipped_roles_satisfy_every_invariant()
        => Assert.Empty(SystemRoleInvariants.Validate(Baseline()));

    [Fact]
    public void The_order_is_employee_manager_hr_admin_super_admin_and_each_role_holds_everything_below_it()
    {
        Assert.Equal(new[] { "employee", "manager", "hr_admin", "super_admin" }, SystemRoleKeys.All);

        var byKey = SystemRoles.Definitions.ToDictionary(d => d.Key, d => d.Permissions);
        for (var i = 1; i < SystemRoleKeys.All.Count; i++)
        {
            foreach (var (module, actions) in byKey[SystemRoleKeys.All[i - 1]])
            {
                Assert.All(actions, a => Assert.True(Holds(byKey[SystemRoleKeys.All[i]], module, a)));
            }
        }
    }

    // ================= exhaustive single changes against independently computed expectations =================

    [Fact]
    public void Removing_any_one_permission_from_any_role_is_refused_exactly_when_it_breaks_the_hierarchy_or_a_required_permission()
    {
        var baseline = Baseline();
        var checkedCases = 0;

        foreach (var key in SystemRoleKeys.All)
        {
            foreach (var (module, actions) in baseline[key])
            {
                foreach (var action in actions)
                {
                    var violations = SystemRoleInvariants.ValidateChange(key, Without(baseline[key], module, action), baseline);

                    var breaksHierarchy = key != SystemRoleKeys.Employee && Holds(baseline[Lower(key)], module, action);
                    var removesRequired = key == SystemRoleKeys.SuperAdmin && SystemRoleInvariants.SuperAdminRequired.Contains((module, action));

                    Assert.Equal(breaksHierarchy || removesRequired, violations.Count > 0);
                    checkedCases++;
                }
            }
        }

        Assert.True(checkedCases > 20);
    }

    [Fact]
    public void Adding_any_catalog_permission_to_any_role_is_refused_exactly_when_the_role_above_lacks_it_or_a_lower_role_would_get_role_management()
    {
        var baseline = Baseline();
        var checkedCases = 0;

        foreach (var key in SystemRoleKeys.All)
        {
            foreach (var (module, actions) in PermissionCatalog.All)
            {
                foreach (var action in actions.Where(a => !Holds(baseline[key], module, a)))
                {
                    var violations = SystemRoleInvariants.ValidateChange(key, With(baseline[key], module, action), baseline);

                    var exceedsHigher = key != SystemRoleKeys.SuperAdmin && !Holds(baseline[Higher(key)], module, action);
                    var rolesForLowerRole = key != SystemRoleKeys.SuperAdmin && module == PermissionNames.Roles.Module;

                    Assert.Equal(exceedsHigher || rolesForLowerRole, violations.Count > 0);
                    checkedCases++;
                }
            }
        }

        Assert.True(checkedCases > 10);
    }

    // ================= named scenarios =================

    [Fact]
    public void A_permission_can_be_moved_down_the_hierarchy_when_the_roles_above_keep_it()
    {
        var baseline = Baseline();

        // Giving a manager a permission HR Admin and Super Admin already hold is a legitimate change.
        Assert.Empty(SystemRoleInvariants.ValidateChange(SystemRoleKeys.Manager, With(baseline[SystemRoleKeys.Manager], PermissionNames.Employees.Module, PermissionNames.Employees.Create), baseline));
    }

    [Fact]
    public void Super_admin_cannot_lose_role_management_or_be_emptied()
    {
        var baseline = Baseline();

        var withoutManage = SystemRoleInvariants.ValidateChange(SystemRoleKeys.SuperAdmin, Without(baseline[SystemRoleKeys.SuperAdmin], PermissionNames.Roles.Module, PermissionNames.Roles.ManagePermissions), baseline);
        var empty = SystemRoleInvariants.ValidateChange(SystemRoleKeys.SuperAdmin, new Dictionary<string, string[]>(), baseline);

        Assert.Contains(withoutManage, v => v.Code == "super_admin_permission_required" && v.RoleKey == SystemRoleKeys.SuperAdmin);
        Assert.Contains(empty, v => v.Code == "super_admin_permission_required");
        Assert.Contains(empty, v => v.Code == "hierarchy_broken");                          // and it no longer contains what HR Admin holds
    }

    [Fact]
    public void Role_management_cannot_be_given_to_a_lower_built_in_role()
    {
        var baseline = Baseline();

        foreach (var key in new[] { SystemRoleKeys.Employee, SystemRoleKeys.Manager, SystemRoleKeys.HrAdmin })
        {
            var violations = SystemRoleInvariants.ValidateChange(key, With(baseline[key], PermissionNames.Roles.Module, PermissionNames.Roles.View), baseline);

            Assert.Contains(violations, v => v.Code == "roles_module_super_admin_only" && v.RoleKey == key);
        }
    }

    [Fact]
    public void An_unknown_permission_is_never_accepted_even_when_it_satisfies_the_hierarchy()
    {
        var baseline = Baseline();
        var everywhere = Baseline().ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string[]>)With(p.Value, "employees", "fly"));

        var violations = SystemRoleInvariants.Validate(everywhere);                          // all four roles hold it, so the hierarchy is intact

        Assert.Equal(4, violations.Count(v => v.Code == "unknown_permission"));
        Assert.DoesNotContain(violations, v => v.Code == "hierarchy_broken");
        Assert.NotEmpty(SystemRoleInvariants.ValidateChange(SystemRoleKeys.Employee, With(baseline[SystemRoleKeys.Employee], "bogus", "x"), baseline));
    }

    [Fact]
    public void Incomplete_input_and_non_built_in_keys_are_reported_and_the_inputs_are_not_modified()
    {
        var baseline = Baseline();
        var incomplete = baseline.Where(p => p.Key != SystemRoleKeys.Manager).ToDictionary(p => p.Key, p => p.Value);
        var snapshot = SystemRoles.Definitions.ToDictionary(d => d.Key, d => d.PermissionsJson);

        Assert.Contains(SystemRoleInvariants.Validate(incomplete), v => v.Code == "missing_role");
        Assert.Equal("not_a_system_role", Assert.Single(SystemRoleInvariants.ValidateChange("custom", new Dictionary<string, string[]>(), baseline)).Code);

        SystemRoleInvariants.ValidateChange(SystemRoleKeys.HrAdmin, new Dictionary<string, string[]>(), baseline);
        Assert.Equal(snapshot, baseline.ToDictionary(p => p.Key, p => RolePermissions.ToJson(p.Value)));   // the "current" set was not changed by the check
    }
}
