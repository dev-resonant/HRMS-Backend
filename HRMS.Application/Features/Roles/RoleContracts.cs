namespace HRMS.Application.Features.Roles
{
    /// <summary>Where a role is defined: shared by every company (the four built-in roles) or owned by the caller's company.</summary>
    public static class RoleScopes
    {
        public const string System = "system";

        public const string Company = "company";
    }

    /// <summary>
    /// A role as exposed by GET /api/roles. Never an EF entity: no company id, no timestamps, no user list.
    /// </summary>
    public sealed class RoleDto
    {
        public Guid Id { get; set; }

        /// <summary>Stable key: employee, manager, hr_admin, super_admin for built-in roles; the role's name for a company role.</summary>
        public string Key { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        /// <summary>True for the built-in roles, which cannot be deleted or renamed (specification Table 2).</summary>
        public bool IsSystem { get; set; }

        /// <summary>
        /// <c>system</c>: a built-in role shared by ALL companies (changing its permissions would affect every company).
        /// <c>company</c>: a custom role that belongs to the caller's company only.
        /// </summary>
        public string Scope { get; set; } = RoleScopes.Company;

        /// <summary>
        /// Module -> allowed actions, as a JSON object (not an escaped string), in canonical order. Only permissions that exist in the
        /// permission catalog are listed; stored values that are malformed or unknown are not reported and grant nothing.
        /// </summary>
        public IReadOnlyDictionary<string, string[]> Permissions { get; set; } = new Dictionary<string, string[]>();
    }
}
