using Microsoft.AspNetCore.Authorization;

namespace HRMS.API.Authorization
{
    /// <summary>
    /// Declares the catalog permission an action requires (module + action, see PermissionNames). The caller must be
    /// authenticated and the permission is read from the database on each request. Several attributes require all of them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequirePermissionAttribute : AuthorizeAttribute
    {
        public RequirePermissionAttribute(string module, string action)
        {
            Module = module;
            Action = action;
        }

        public string Module { get; }

        public string Action { get; }
    }

    /// <summary>
    /// Explicit declaration for actions that act only on the caller's own session or identity and so need no catalog
    /// permission (for example /api/auth/me and logout). By default the active account is also verified; pass
    /// <c>requireActiveAccount: false</c> only where a deactivated or inconsistent account must still be able to act
    /// (logout). Use sparingly: a convention test pins the list of actions allowed to use it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SelfServiceAttribute : AuthorizeAttribute
    {
        public SelfServiceAttribute(bool requireActiveAccount = true)
        {
            RequireActiveAccount = requireActiveAccount;
        }

        public bool RequireActiveAccount { get; }

        /// <summary>
        /// Whether an account that still has to set its password (is_first_login) may call this action. Default false: such an account
        /// is limited to change-password, /api/auth/me and logout, and every other protected endpoint returns 403.
        /// </summary>
        public bool AllowWhilePasswordChangePending { get; set; }
    }

    /// <summary>
    /// Marks an action (or a whole controller) as a PLATFORM operation: reachable only by a caller whose company is the designated platform
    /// company (<see cref="HRMS.Application.Interfaces.ICurrentUser.IsPlatform"/>, read from the database) AND who holds every
    /// <see cref="RequirePermissionAttribute"/> on the action. It is deliberately NOT an authorization attribute of its own: it never grants
    /// anything and never replaces a permission; it can only add a requirement. An action that carries it must also carry at least one
    /// [RequirePermission] (otherwise it is a declaration conflict and is denied for everyone), must not be anonymous or self-service, and must
    /// live under the <c>/api/platform</c> route area - and every action under that area must carry it (enforced at runtime and by convention tests).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public sealed class PlatformOnlyAttribute : Attribute
    {
        /// <summary>The route prefix reserved for platform operations.</summary>
        public const string RoutePrefix = "/api/platform";
    }
}
