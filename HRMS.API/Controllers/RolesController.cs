using HRMS.API.Authorization;
using HRMS.Application.Features.Roles;
using HRMS.Application.Features.Roles.Queries.ListRoles;
using HRMS.Domain.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Controllers
{
    /// <summary>
    /// Roles (specification API table: GET /roles, Super Admin). READ-ONLY for now: changing permissions, role assignment and custom-role
    /// management are separate, not yet approved work. The company always comes from the authenticated caller.
    /// </summary>
    [ApiController]
    [Route("api/roles")]
    [Produces("application/json")]
    public class RolesController : ControllerBase
    {
        private readonly IMediator _mediator;

        public RolesController(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>
        /// List the roles and their permissions: the four built-in roles (<c>scope = system</c>, shared by every company) first, in hierarchy order,
        /// then the caller's own company's custom roles (<c>scope = company</c>). Another company's roles are never returned and no company
        /// parameter is accepted.
        /// </summary>
        [HttpGet]
        [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.View)]
        [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new ListRolesQuery(), cancellationToken));
    }
}
