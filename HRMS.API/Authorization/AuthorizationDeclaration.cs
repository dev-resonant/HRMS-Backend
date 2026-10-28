using Microsoft.AspNetCore.Authorization;

namespace HRMS.API.Authorization
{
    public enum DeclarationKind
    {
        /// <summary>Nothing (or only a bare [Authorize]) is declared: denied by default.</summary>
        Undeclared,

        /// <summary>Only [AllowAnonymous]: open to everyone.</summary>
        Anonymous,

        /// <summary>Exactly one of [RequirePermission] (one or more) or [SelfService]: authentication is always required.</summary>
        Protected,

        /// <summary>Contradictory declarations. Always denied; never treated as permission to bypass anything.</summary>
        Conflict
    }

    /// <summary>
    /// The single rule that interprets the authorization metadata of an action (class-level and action-level combined).
    /// Used by <see cref="PermissionAuthorizationFilter"/> at runtime and by the convention tests, so they cannot diverge.
    /// [AllowAnonymous] never wins over a permission or self-service declaration: the combination is a Conflict and is denied.
    /// </summary>
    public static class AuthorizationDeclaration
    {
        public static DeclarationKind Classify(IEnumerable<object> metadata)
        {
            var items = metadata.ToList();

            var anonymous = items.OfType<IAllowAnonymous>().Any();
            var permission = items.OfType<RequirePermissionAttribute>().Any();
            var selfService = items.OfType<SelfServiceAttribute>().Any();
            var bareAuthorize = items.OfType<IAuthorizeData>().Any(a => a is not RequirePermissionAttribute and not SelfServiceAttribute);
            var platformOnly = items.OfType<PlatformOnlyAttribute>().Any();

            if (permission && selfService)
            {
                return DeclarationKind.Conflict;
            }

            // [PlatformOnly] only ever ADDS a requirement, so it needs a permission next to it and cannot combine with anonymous or
            // self-service access. Anything else is a contradiction and is denied for everyone.
            if (platformOnly && (!permission || selfService || anonymous))
            {
                return DeclarationKind.Conflict;
            }

            if (anonymous)
            {
                // Anonymous is only valid on its own. Any authorization requirement next to it is contradictory.
                return permission || selfService || bareAuthorize ? DeclarationKind.Conflict : DeclarationKind.Anonymous;
            }

            return permission || selfService ? DeclarationKind.Protected : DeclarationKind.Undeclared;
        }
    }
}
