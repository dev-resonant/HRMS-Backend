using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Employees;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Departments.Commands.ReassignEmployees
{
    /// <summary>
    /// POST /api/departments/{id}/reassign-employees (specification 5.3: "HR can bulk-reassign employees between departments").
    /// {id} is the source department. All-or-nothing: every id is validated before anything is changed, all changes and one audit entry
    /// per employee (old and new department id) are saved in ONE transaction. Employees keep their status; offboarded employees can be
    /// moved too (otherwise a department could never be emptied). Uses the existing departments.manage permission.
    /// </summary>
    [AuditExpectation("employee", "employee.department_changed")]
    public sealed record ReassignEmployeesCommand(Guid SourceDepartmentId, Guid ToDepartmentId, IReadOnlyList<Guid>? EmployeeIds) : IRequest<ReassignEmployeesResponse>, IAuditedRequest;

    public sealed class ReassignEmployeesCommandValidator : AbstractValidator<ReassignEmployeesCommand>
    {
        public ReassignEmployeesCommandValidator()
        {
            RuleFor(c => c.SourceDepartmentId).NotEmpty();
            RuleFor(c => c.ToDepartmentId).NotEmpty();

            RuleFor(c => c.ToDepartmentId).NotEqual(c => c.SourceDepartmentId)
                .WithMessage("ToDepartmentId must be different from the source department.");

            RuleFor(c => c.EmployeeIds).NotNull().WithMessage("EmployeeIds is required.");

            When(c => c.EmployeeIds is not null, () =>
            {
                RuleFor(c => c.EmployeeIds!).NotEmpty().WithMessage("EmployeeIds must contain at least one employee.")
                    .Must(ids => ids.Count <= ReassignEmployeesRequest.MaxReassignedEmployees)
                    .WithMessage($"At most {ReassignEmployeesRequest.MaxReassignedEmployees} employees can be reassigned in one request.")
                    .Must(ids => ids.All(id => id != Guid.Empty)).WithMessage("EmployeeIds must not contain an empty id.")
                    .Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("EmployeeIds must not contain duplicates.");
            });
        }
    }

    public sealed class ReassignEmployeesCommandHandler : IRequestHandler<ReassignEmployeesCommand, ReassignEmployeesResponse>
    {
        private readonly IDepartmentRepository _departments;
        private readonly IEmployeeRepository _employees;
        private readonly IReferenceValidator _references;
        private readonly IAuditWriter _audit;

        public ReassignEmployeesCommandHandler(
            IDepartmentRepository departments, IEmployeeRepository employees, IReferenceValidator references, IAuditWriter audit)
        {
            _departments = departments;
            _employees = employees;
            _references = references;
            _audit = audit;
        }

        public async Task<ReassignEmployeesResponse> Handle(ReassignEmployeesCommand command, CancellationToken cancellationToken)
        {
            var ids = command.EmployeeIds!;

            // Source: a department of the caller's company, else 404 (the route id). Destination: same company, else the usual 400.
            _ = await _departments.GetForUpdateAsync(command.SourceDepartmentId, cancellationToken)
                ?? throw new NotFoundException(DepartmentRules.NotFoundMessage);

            await _references.EnsureDepartmentInCompanyAsync(command.ToDepartmentId, "ToDepartmentId", cancellationToken);

            // Company-bound read: a missing id and another company's id are both simply absent.
            var employees = await _employees.GetManyForUpdateAsync(ids, cancellationToken);

            if (employees.Count != ids.Count)
            {
                throw EmployeeRules.Invalid("EmployeeIds", "EmployeeIds contains an employee that does not exist.");
            }

            if (employees.Any(e => e.DepartmentId != command.SourceDepartmentId))
            {
                throw EmployeeRules.Invalid("EmployeeIds", "EmployeeIds contains an employee who is not in the source department.");
            }

            // Everything is valid; only now is anything changed. The employee's xmin concurrency token makes a concurrent edit or a
            // second reassignment of the same employee fail the whole save (409) instead of being overwritten.
            foreach (var employee in employees.OrderBy(e => e.Id))
            {
                var from = employee.DepartmentId;

                employee.DepartmentId = command.ToDepartmentId;

                _audit.Stage(new AuditEntry(
                    "employee", "employee.department_changed", employee.Id,
                    new { DepartmentId = from }, new { DepartmentId = command.ToDepartmentId }));
            }

            await _employees.SaveChangesAsync(cancellationToken);

            return new ReassignEmployeesResponse
            {
                FromDepartmentId = command.SourceDepartmentId,
                ToDepartmentId = command.ToDepartmentId,
                ReassignedCount = employees.Count,
                EmployeeIds = employees.Select(e => e.Id).OrderBy(id => id).ToList()
            };
        }
    }
}
