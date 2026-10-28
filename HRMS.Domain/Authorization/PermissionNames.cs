namespace HRMS.Domain.Authorization
{
    /// <summary>
    /// The approved Phase 1 permission catalog (decision D3). Permissions are stored per role as JSON
    /// <c>{ "module": ["action", ...] }</c>. Anything not listed here is unknown and always denies.
    /// </summary>
    public static class PermissionNames
    {
        public static class Employees
        {
            public const string Module = "employees";
            public const string ViewOwn = "view_own";
            public const string ViewTeam = "view_team";
            public const string ViewAll = "view_all";
            public const string Create = "create";
            public const string EditOwn = "edit_own";
            public const string EditAny = "edit_any";
            public const string Deactivate = "deactivate";
        }

        public static class Departments
        {
            public const string Module = "departments";
            public const string ViewOwnDepartment = "view_own_department";
            public const string ViewAll = "view_all";
            public const string Manage = "manage";
        }

        public static class Roles
        {
            public const string Module = "roles";
            public const string View = "view";
            public const string ManagePermissions = "manage_permissions";
        }

        public static class SystemSettings
        {
            public const string Module = "system";
            public const string ConfigureCompany = "configure_company";
        }
    }

    public static class PermissionCatalog
    {
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> All = new Dictionary<string, IReadOnlyList<string>>
        {
            [PermissionNames.Employees.Module] = new[]
            {
                PermissionNames.Employees.ViewOwn, PermissionNames.Employees.ViewTeam, PermissionNames.Employees.ViewAll,
                PermissionNames.Employees.Create, PermissionNames.Employees.EditOwn, PermissionNames.Employees.EditAny,
                PermissionNames.Employees.Deactivate
            },
            [PermissionNames.Departments.Module] = new[]
            {
                PermissionNames.Departments.ViewOwnDepartment, PermissionNames.Departments.ViewAll, PermissionNames.Departments.Manage
            },
            [PermissionNames.Roles.Module] = new[]
            {
                PermissionNames.Roles.View, PermissionNames.Roles.ManagePermissions
            },
            [PermissionNames.SystemSettings.Module] = new[]
            {
                PermissionNames.SystemSettings.ConfigureCompany
            }
        };

        public static bool IsValid(string? module, string? action)
            => module is not null && action is not null
               && All.TryGetValue(module, out var actions)
               && actions.Contains(action);
    }
}
