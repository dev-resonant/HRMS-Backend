using HRMS.API.Authorization;
using HRMS.Application.Features.Departments;
using HRMS.Application.Features.Departments.Commands.CreateDepartment;
using HRMS.Application.Features.Departments.Commands.DeleteDepartment;
using HRMS.Application.Features.Departments.Commands.ReassignEmployees;
using HRMS.Application.Features.Departments.Commands.UpdateDepartment;
using HRMS.Application.Features.Departments.Queries.GetDepartment;
using HRMS.Application.Features.Departments.Queries.ListDepartments;
using HRMS.Domain.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Controllers
{
    /// <summary>
    /// Departments (specification 5.3 and the API table). The company always comes from the authenticated caller. HR Admin manages
    /// departments; every role can read: HR the whole company, managers and employees their own department. Errors: 400 validation,
    /// 401 unauthenticated, 403 missing permission, 404 missing / other company / out of scope, 409 conflict.
    /// DELETE is allowed only for a department with no employees and no sub-departments (409 otherwise); employees are moved in bulk with
    /// POST {id}/reassign-employees. Both need departments.manage.
    /// </summary>
    [ApiController]
    [Route("api/departments")]
    [Produces("application/json")]
    public class DepartmentsController : ControllerBase
    {
        private readonly IMediator _mediator;

        public DepartmentsController(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>List the departments the caller may see (HR: the whole company; manager and employee: their own department), ordered by name. Build the tree from <c>parentDepartmentId</c>.</summary>
        [HttpGet]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.ViewOwnDepartment)]
        [ProducesResponseType(typeof(IReadOnlyList<DepartmentDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<IReadOnlyList<DepartmentDto>>> List(CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new ListDepartmentsQuery(), cancellationToken));

        /// <summary>Get one department within the caller's scope. Missing, other-company and out-of-scope departments all answer 404.</summary>
        [HttpGet("{id:guid}", Name = "GetDepartment")]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.ViewOwnDepartment)]
        [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<DepartmentDto>> Get(Guid id, CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new GetDepartmentQuery(id), cancellationToken));

        /// <summary>Create a department (HR Admin) with an optional parent and head of the same company.</summary>
        [HttpPost]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.Manage)]
        [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<DepartmentDto>> Create([FromBody] CreateDepartmentRequest request, CancellationToken cancellationToken)
        {
            var created = await _mediator.Send(new CreateDepartmentCommand(request.Name, request.ParentDepartmentId, request.HeadEmployeeId), cancellationToken);

            return CreatedAtRoute("GetDepartment", new { id = created.Id }, created);
        }

        /// <summary>Update name, parent and head (HR Admin). The body replaces all three; null clears the parent or head. A department cannot be nested under itself or its own sub-departments.</summary>
        [HttpPut("{id:guid}")]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.Manage)]
        [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<DepartmentDto>> Update(Guid id, [FromBody] UpdateDepartmentRequest request, CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new UpdateDepartmentCommand(id, request.Name, request.ParentDepartmentId, request.HeadEmployeeId), cancellationToken));

        /// <summary>Delete a department (HR Admin) that has no employees assigned and no sub-departments; otherwise 409. Nothing is cascaded or moved.</summary>
        [HttpDelete("{id:guid}")]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.Manage)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _mediator.Send(new DeleteDepartmentCommand(id), cancellationToken);

            return NoContent();
        }

        /// <summary>
        /// Move employees out of the department {id} into another one (HR Admin), all or nothing. 400 when the destination is missing or foreign,
        /// equals the source, or any employee is missing, foreign, duplicated or not in the source department; 404 for an unknown source; 409 when an employee
        /// was changed concurrently.
        /// </summary>
        [HttpPost("{id:guid}/reassign-employees")]
        [RequirePermission(PermissionNames.Departments.Module, PermissionNames.Departments.Manage)]
        [ProducesResponseType(typeof(ReassignEmployeesResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ReassignEmployeesResponse>> ReassignEmployees(Guid id, [FromBody] ReassignEmployeesRequest request, CancellationToken cancellationToken)
            => Ok(await _mediator.Send(new ReassignEmployeesCommand(id, request.ToDepartmentId, request.EmployeeIds), cancellationToken));
    }
}
