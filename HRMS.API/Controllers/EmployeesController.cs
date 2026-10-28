using HRMS.API.Authorization;
using HRMS.API.Contracts;
using HRMS.API.Extensions;
using HRMS.API.RateLimiting;
using HRMS.Application.Features.Employees;
using HRMS.Application.Features.Employees.Commands.CreateEmployee;
using HRMS.Application.Features.Employees.Commands.DeactivateEmployee;
using HRMS.Application.Features.Employees.Commands.IssueInvitation;
using HRMS.Application.Features.Employees.Commands.UpdateEmployee;
using HRMS.Application.Features.Employees.Queries.GetEmployee;
using HRMS.Domain.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace HRMS.API.Controllers
{
    /// <summary>
    /// Employee management (specification 5, API table). Thin: each action declares its permission, builds a command from the
    /// body/route (company, actor and audit attribution are never taken from the client) and returns the DTO.
    /// Errors: 400 validation, 401 unauthenticated, 403 missing permission, 404 missing or not visible, 409 conflict,
    /// 412 stale If-Match. Single-employee responses carry an ETag; PUT accepts an optional If-Match.
    /// </summary>
    [ApiController]
    [Route("api/employees")]
    [Produces("application/json")]
    public class EmployeesController : ControllerBase
    {
        private readonly IMediator _mediator;

        public EmployeesController(IMediator mediator)
        {
            _mediator = mediator;
        }

        private void SetETag(uint version) => Response.Headers[HeaderNames.ETag] = ETagHeaders.For(version);

        /// <summary>List employees visible to the caller (HR: whole company; manager: self and direct reports). Employees get 403.</summary>
        [HttpGet]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam)]
        [ProducesResponseType(typeof(PagedResult<EmployeeDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<PagedResult<EmployeeDto>>> List([FromQuery] ListEmployeesRequest request, CancellationToken cancellationToken)
            => Ok(await _mediator.Send(request.ToQuery(), cancellationToken));

        /// <summary>Get one employee within the caller's scope (own record, direct reports, or the company). Returns an ETag.</summary>
        [HttpGet("{id:guid}", Name = "GetEmployee")]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn)]
        [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<EmployeeDto>> Get(Guid id, CancellationToken cancellationToken)
        {
            var employee = await _mediator.Send(new GetEmployeeQuery(id), cancellationToken);
            SetETag(employee.Version);

            return Ok(employee);
        }

        /// <summary>
        /// Create the login account and the employee record (HR Admin). The employee code is generated; the status starts as active.
        /// A one-time password-setup invitation is issued; its token is never returned (it goes to the delivery channel, if one is configured).
        /// </summary>
        [HttpPost]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.Create)]
        [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<EmployeeDto>> Create([FromBody] CreateEmployeeRequest request, CancellationToken cancellationToken)
        {
            var created = await _mediator.Send(
                new CreateEmployeeCommand(
                    request.Name, request.Email, request.DepartmentId, request.ManagerId, request.Designation, request.JoiningDate,
                    request.PersonalEmail, request.Phone, request.EmploymentType),
                cancellationToken);

            SetETag(created.Version);

            return CreatedAtRoute("GetEmployee", new { id = created.Id }, created);
        }

        /// <summary>
        /// Replace the editable profile fields of an employee (HR Admin). Optional If-Match: if the employee changed since the ETag
        /// the client holds, nothing is changed and the answer is 412.
        /// </summary>
        [HttpPut("{id:guid}")]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.EditAny)]
        [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
        public async Task<ActionResult<EmployeeDto>> Update(Guid id, [FromBody] UpdateEmployeeRequest request, CancellationToken cancellationToken)
        {
            var updated = await _mediator.Send(
                new UpdateEmployeeCommand(
                    id, request.Name, request.DepartmentId, request.ManagerId, request.Designation, request.JoiningDate, request.Status,
                    request.LastWorkingDate, request.PersonalEmail, request.Phone, request.EmploymentType, ETagHeaders.ReadIfMatch(Request)),
                cancellationToken);

            SetETag(updated.Version);

            return Ok(updated);
        }

        /// <summary>Offboard an employee (HR Admin): inactive, login blocked, every session deleted. The record is kept. Optional If-Match.</summary>
        [HttpPut("{id:guid}/deactivate")]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.Deactivate)]
        [ProducesResponseType(typeof(DeactivateEmployeeResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
        public async Task<ActionResult<DeactivateEmployeeResponse>> Deactivate(Guid id, [FromBody] DeactivateEmployeeRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new DeactivateEmployeeCommand(id, request.LastWorkingDate, ETagHeaders.ReadIfMatch(Request)), cancellationToken);

            SetETag(result.Employee.Version);

            return Ok(result);
        }

        /// <summary>
        /// Issue a new one-time password-setup invitation (HR Admin) for an employee whose account has not set a password yet; the
        /// earlier open invitation is revoked. The token is never returned: <c>deliveryConfigured</c> tells whether anything was sent.
        /// </summary>
        [HttpPost("{id:guid}/invitation")]
        [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.Create)]
        [AuthRateLimit(AuthRateLimitPolicy.InvitationReissue)]
        [ProducesResponseType(typeof(InvitationIssuedResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<InvitationIssuedResponse>> IssueInvitation(Guid id, CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new IssueInvitationCommand(id), cancellationToken));
    }
}
