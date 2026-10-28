namespace HRMS.Domain.Authorization
{
    public static class SystemRoleKeys
    {
        public const string Employee = "employee";
        public const string Manager = "manager";
        public const string HrAdmin = "hr_admin";
        public const string SuperAdmin = "super_admin";

        public static readonly IReadOnlyList<string> All = new[] { Employee, Manager, HrAdmin, SuperAdmin };
    }

    public sealed record SystemRoleDefinition(Guid Id, string Key, string DisplayName, IReadOnlyDictionary<string, string[]> Permissions)
    {
        /// <summary>Compact canonical JSON stored in <c>Roles.Permissions</c> (jsonb).</summary>
        public string PermissionsJson => RolePermissions.ToJson(Permissions);
    }

    /// <summary>
    /// The four built-in roles (spec section 2): each inherits everything below it. System roles have CompanyId = NULL and
    /// IsSystem = true. The migration that seeds them contains the same JSON as a literal; a test keeps the two identical.
    /// </summary>
    public static class SystemRoles
    {
        // Fixed ids so every database (and the migration) agrees on the identity of a system role.
        public static readonly Guid EmployeeRoleId = new("5a000000-0000-4000-8000-000000000001");
        public static readonly Guid ManagerRoleId = new("5a000000-0000-4000-8000-000000000002");
        public static readonly Guid HrAdminRoleId = new("5a000000-0000-4000-8000-000000000003");
        public static readonly Guid SuperAdminRoleId = new("5a000000-0000-4000-8000-000000000004");

        private static readonly string[] EmployeeEmployees =
        {
            PermissionNames.Employees.ViewOwn, PermissionNames.Employees.EditOwn
        };

        private static readonly string[] ManagerEmployees =
        {
            PermissionNames.Employees.ViewOwn, PermissionNames.Employees.EditOwn, PermissionNames.Employees.ViewTeam
        };

        private static readonly string[] HrEmployees =
        {
            PermissionNames.Employees.ViewOwn, PermissionNames.Employees.EditOwn, PermissionNames.Employees.ViewTeam,
            PermissionNames.Employees.ViewAll, PermissionNames.Employees.Create, PermissionNames.Employees.EditAny,
            PermissionNames.Employees.Deactivate
        };

        public static readonly IReadOnlyList<SystemRoleDefinition> Definitions = new[]
        {
            new SystemRoleDefinition(EmployeeRoleId, SystemRoleKeys.Employee, "Employee", new Dictionary<string, string[]>
            {
                [PermissionNames.Employees.Module] = EmployeeEmployees,
                [PermissionNames.Departments.Module] = new[] { PermissionNames.Departments.ViewOwnDepartment }
            }),
            new SystemRoleDefinition(ManagerRoleId, SystemRoleKeys.Manager, "Manager", new Dictionary<string, string[]>
            {
                [PermissionNames.Employees.Module] = ManagerEmployees,
                [PermissionNames.Departments.Module] = new[] { PermissionNames.Departments.ViewOwnDepartment }
            }),
            new SystemRoleDefinition(HrAdminRoleId, SystemRoleKeys.HrAdmin, "HR Administrator", new Dictionary<string, string[]>
            {
                [PermissionNames.Employees.Module] = HrEmployees,
                [PermissionNames.Departments.Module] = new[]
                {
                    PermissionNames.Departments.ViewOwnDepartment, PermissionNames.Departments.ViewAll, PermissionNames.Departments.Manage
                },
                [PermissionNames.SystemSettings.Module] = new[] { PermissionNames.SystemSettings.ConfigureCompany }
            }),
            new SystemRoleDefinition(SuperAdminRoleId, SystemRoleKeys.SuperAdmin, "Super Administrator", new Dictionary<string, string[]>
            {
                [PermissionNames.Employees.Module] = HrEmployees,
                [PermissionNames.Departments.Module] = new[]
                {
                    PermissionNames.Departments.ViewOwnDepartment, PermissionNames.Departments.ViewAll, PermissionNames.Departments.Manage
                },
                [PermissionNames.Roles.Module] = new[] { PermissionNames.Roles.View, PermissionNames.Roles.ManagePermissions },
                [PermissionNames.SystemSettings.Module] = new[] { PermissionNames.SystemSettings.ConfigureCompany }
            })
        };

        public static SystemRoleDefinition? Find(string key) => Definitions.FirstOrDefault(d => d.Key == key);
    }
}
