using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Departments.Commands.UpdateDepartment
{
    /// <summary>
    /// PUT /api/departments/{id} (API table: "Update department name / head / parent", HR Admin). Replaces the three fields; null clears the
    /// parent or head. Re-parenting is serialized per company and refuses cycles. Atomic with its audit entry (before and after state).
    /// </summary>
    [AuditExpectation("department", "department.updated")]
    public sealed record UpdateDepartmentCommand(Guid DepartmentId, string Name, Guid? ParentDepartmentId, Guid? HeadEmployeeId) : IRequest<DepartmentDto>, IAuditedRequest;

    public sealed class UpdateDepartmentCommandValidator : AbstractValidator<UpdateDepartmentCommand>
    {
        public UpdateDepartmentCommandValidator()
        {
            RuleFor(c => c.DepartmentId).NotEmpty();
            RuleFor(c => c.Name).ValidName();
            RuleFor(c => c.ParentDepartmentId).NotEqual(Guid.Empty).When(c => c.ParentDepartmentId.HasValue);
            RuleFor(c => c.HeadEmployeeId).NotEqual(Guid.Empty).When(c => c.HeadEmployeeId.HasValue);
        }
    }

    public sealed class UpdateDepartmentCommandHandler : IRequestHandler<UpdateDepartmentCommand, DepartmentDto>
    {
        private readonly IDepartmentRepository _departments;
        private readonly IDepartmentQueries _queries;
        private readonly IEmployeeRepository _employees;
        private readonly IReferenceValidator _references;
        private readonly IAuditWriter _audit;

        public UpdateDepartmentCommandHandler(
            IDepartmentRepository departments, IDepartmentQueries queries, IEmployeeRepository employees,
            IReferenceValidator references, IAuditWriter audit)
        {
            _departments = departments;
            _queries = queries;
            _employees = employees;
            _references = references;
            _audit = audit;
        }

        public async Task<DepartmentDto> Handle(UpdateDepartmentCommand command, CancellationToken cancellationToken)
        {
            // Two concurrent re-parentings could each pass the cycle check and together create a cycle: take the company's hierarchy
            // lock before reading anything the check depends on. Held until the transaction ends.
            await _departments.LockHierarchyAsync(cancellationToken);

            var department = await _departments.GetForUpdateAsync(command.DepartmentId, cancellationToken)
                ?? throw new NotFoundException(DepartmentRules.NotFoundMessage);

            if (command.ParentDepartmentId is { } parentId)
            {
                await DepartmentRules.EnsureParentAssignableAsync(_references, _departments, department.Id, parentId, cancellationToken);
            }

            if (command.HeadEmployeeId is { } headId)
            {
                // Keeping the current head must not be blocked because that person was offboarded since.
                await DepartmentRules.EnsureHeadAssignableAsync(_references, _employees, headId, requireActive: headId != department.HeadEmpId, cancellationToken);
            }

            var before = DepartmentRules.Snapshot(department);

            department.Name = command.Name.Trim();
            department.ParentDeptId = command.ParentDepartmentId;
            department.HeadEmpId = command.HeadEmployeeId;

            _audit.Stage(new AuditEntry("department", "department.updated", department.Id, before, DepartmentRules.Snapshot(department)));

            await _departments.SaveChangesAsync(cancellationToken);

            return await _queries.GetInCompanyAsync(department.Id, cancellationToken)
                ?? throw new InvalidOperationException("The department that was just updated could not be read back.");
        }
    }
}
