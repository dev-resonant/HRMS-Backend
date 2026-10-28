using System.Security.Claims;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HRMS.API.Authorization
{
    /// <summary>
    /// Global deny-by-default authorization for every controller action. An action must be [AllowAnonymous], carry at least
    /// one [RequirePermission], or be explicitly [SelfService]; anything else is rejected with 403 even for an authenticated
    /// caller. Permissions are evaluated against the database on every request (see <see cref="ICurrentUserLoader"/>).
    /// </summary>
    public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
    {
        private readonly ICurrentUserLoader _loader;
        private readonly ICurrentUser _currentUser;

        public PermissionAuthorizationFilter(ICurrentUserLoader loader, ICurrentUser currentUser)
        {
            _loader = loader;
            _currentUser = currentUser;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var metadata = context.ActionDescriptor.EndpointMetadata;

            // [AllowAnonymous] is honoured only when it stands alone. Next to [RequirePermission] / [SelfService] (at the
            // controller or the action) it is a contradiction and the request is denied for everyone - the declaration is
            // never allowed to switch a required permission off.
            var kind = AuthorizationDeclaration.Classify(metadata);

            if (kind == DeclarationKind.Conflict)
            {
                throw new ForbiddenException("Access denied.");
            }

            if (kind == DeclarationKind.Anonymous)
            {
                return;
            }

            var platformOnly = metadata.OfType<PlatformOnlyAttribute>().Any();

            // The /api/platform route area is reserved: an action there WITHOUT [PlatformOnly] (a forgotten attribute) is refused outright,
            // for everyone, instead of being served as an ordinary tenant endpoint.
            if (!platformOnly && context.HttpContext.Request.Path.StartsWithSegments(PlatformOnlyAttribute.RoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ForbiddenException("Access denied.");
            }

            var user = context.HttpContext.User;

            if (user.Identity?.IsAuthenticated != true)
            {
                throw new UnauthorizedAccessException("Authentication is required.");
            }

            var requirements = metadata.OfType<RequirePermissionAttribute>().ToList();
            var selfService = metadata.OfType<SelfServiceAttribute>().FirstOrDefault();

            if (kind == DeclarationKind.Undeclared)
            {
                throw new ForbiddenException("Access denied.");
            }

            if (requirements.Count == 0 && selfService is { RequireActiveAccount: false })
            {
                return;
            }

            if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                || !Guid.TryParse(user.FindFirstValue("companyId"), out var companyId))
            {
                throw new UnauthorizedAccessException("Invalid user identity.");
            }

            var cancellationToken = context.HttpContext.RequestAborted;

            switch (await _loader.LoadAsync(userId, companyId, cancellationToken))
            {
                case CurrentUserLoadResult.Loaded:
                    break;
                case CurrentUserLoadResult.NotFound:
                    throw new UnauthorizedAccessException("User not found.");
                case CurrentUserLoadResult.Inactive:
                    throw new ForbiddenException("Account is deactivated.");
                default:
                    throw new ForbiddenException("Access denied.");
            }

            // First-login enforcement (specification: "redirect to password reset screen before any other page"): enforced here, for
            // every protected action at once, so no other endpoint can be used to get around it. Only the self-service actions that
            // explicitly allow it (change-password, me) pass; permission-protected actions never do.
            if (_currentUser.PasswordChangeRequired && !(requirements.Count == 0 && selfService is { AllowWhilePasswordChangePending: true }))
            {
                throw new ForbiddenException("You must change your password before using the system.");
            }

            // Platform gate: the caller's company must be the designated platform company (database state, loaded above). This is checked IN
            // ADDITION to every permission below, never instead of them; a role name or a token claim cannot satisfy it. The answer is the same
            // generic 403 as any other denial, so it does not reveal whether a platform company exists.
            if (platformOnly && !_currentUser.IsPlatform)
            {
                throw new ForbiddenException("Access denied.");
            }

            foreach (var requirement in requirements)
            {
                if (!_currentUser.HasPermission(requirement.Module, requirement.Action))
                {
                    throw new ForbiddenException("You do not have permission to perform this action.");
                }
            }
        }
    }
}
