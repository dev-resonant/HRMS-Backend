namespace HRMS.Domain.Authorization
{
    /// <summary>A rule of the built-in role set that a proposed set of permissions breaks.</summary>
    public sealed record SystemRoleViolation(string RoleKey, string Code, string Message);

    /// <summary>
    /// Invariants of the four built-in roles, checked on a COMPLETE proposal (all four roles with the permissions they would have after
    /// a change). They are derived from the catalog and the role order in <see cref="SystemRoleKeys.All"/>; no permission list is repeated here.
    /// <list type="bullet">
    /// <item>Every role is present and holds only catalog permissions (an unknown permission is never accepted to satisfy another rule).</item>
    /// <item>Hierarchy (specification section 2: "each role inherits all capabilities of lower roles"): employee ⊆ manager ⊆ hr_admin ⊆ super_admin.</item>
    /// <item>Super Admin keeps <see cref="SuperAdminRequired"/> (otherwise nobody could manage roles any more).</item>
    /// <item>Role management stays with Super Admin only (specification matrix: "Manage roles &amp; permissions" is Super Admin only), so no lower
    /// built-in role may hold any <c>roles</c> permission.</item>
    /// </list>
    /// Custom company roles are not covered: they are not part of the hierarchy and their limits depend on who edits them (a later decision).
    /// </summary>
    public static class SystemRoleInvariants
    {
        public static readonly IReadOnlyList<(string Module, string Action)> SuperAdminRequired = new[]
        {
            (PermissionNames.Roles.Module, PermissionNames.Roles.View),
            (PermissionNames.Roles.Module, PermissionNames.Roles.ManagePermissions)
        };

        /// <summary>Violations of the invariants by the proposed permissions of all four built-in roles (empty = acceptable).</summary>
        public static IReadOnlyList<SystemRoleViolation> Validate(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> permissionsByRoleKey)
        {
            ArgumentNullException.ThrowIfNull(permissionsByRoleKey);

            var violations = new List<SystemRoleViolation>();

            foreach (var key in SystemRoleKeys.All)
            {
                if (!permissionsByRoleKey.ContainsKey(key))
                {
                    violations.Add(new SystemRoleViolation(key, "missing_role", $"The permissions of the built-in role '{key}' are required to check the hierarchy."));
                }
            }

            if (violations.Count > 0)
            {
                return violations;
            }

            foreach (var key in SystemRoleKeys.All)
            {
                foreach (var (module, actions) in permissionsByRoleKey[key])
                {
                    foreach (var action in actions)
                    {
                        if (!PermissionCatalog.IsValid(module, action))
                        {
                            violations.Add(new SystemRoleViolation(key, "unknown_permission", $"'{key}' holds a permission that is not in the permission catalog."));
                        }
                    }
                }
            }

            for (var i = 1; i < SystemRoleKeys.All.Count; i++)
            {
                var lower = SystemRoleKeys.All[i - 1];
                var higher = SystemRoleKeys.All[i];

                foreach (var (module, actions) in permissionsByRoleKey[lower])
                {
                    foreach (var action in actions)
                    {
                        if (!Holds(permissionsByRoleKey[higher], module, action))
                        {
                            violations.Add(new SystemRoleViolation(higher, "hierarchy_broken",
                                $"'{higher}' must keep every permission of '{lower}' (missing {module}.{action})."));
                        }
                    }
                }
            }

            foreach (var (module, action) in SuperAdminRequired)
            {
                if (!Holds(permissionsByRoleKey[SystemRoleKeys.SuperAdmin], module, action))
                {
                    violations.Add(new SystemRoleViolation(SystemRoleKeys.SuperAdmin, "super_admin_permission_required",
                        $"'{SystemRoleKeys.SuperAdmin}' must keep {module}.{action}."));
                }
            }

            foreach (var key in SystemRoleKeys.All.Where(k => k != SystemRoleKeys.SuperAdmin))
            {
                if (permissionsByRoleKey[key].TryGetValue(PermissionNames.Roles.Module, out var roleActions) && roleActions.Length > 0)
                {
                    violations.Add(new SystemRoleViolation(key, "roles_module_super_admin_only",
                        $"Role management permissions are reserved for '{SystemRoleKeys.SuperAdmin}'; '{key}' must not hold any."));
                }
            }

            return violations;
        }

        /// <summary>
        /// Violations if the built-in role <paramref name="roleKey"/> were given <paramref name="proposed"/> while the other three keep
        /// <paramref name="current"/>. A key that is not a built-in role is itself a violation.
        /// </summary>
        public static IReadOnlyList<SystemRoleViolation> ValidateChange(
            string roleKey,
            IReadOnlyDictionary<string, string[]> proposed,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> current)
        {
            ArgumentNullException.ThrowIfNull(proposed);
            ArgumentNullException.ThrowIfNull(current);

            if (!SystemRoleKeys.All.Contains(roleKey))
            {
                return new[] { new SystemRoleViolation(roleKey, "not_a_system_role", "Only the built-in roles take part in the hierarchy.") };
            }

            var after = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(current, StringComparer.Ordinal) { [roleKey] = proposed };

            return Validate(after);
        }

        private static bool Holds(IReadOnlyDictionary<string, string[]> permissions, string module, string action)
            => permissions.TryGetValue(module, out var actions) && actions.Contains(action, StringComparer.Ordinal);
    }
}
