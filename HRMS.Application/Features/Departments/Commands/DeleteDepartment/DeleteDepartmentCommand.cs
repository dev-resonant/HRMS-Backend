using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Departments.Commands.DeleteDepartment
{
    /// <summary>
    /// DELETE /api/departments/{id}. Approved rule (the specification names DELETE but does not define it): a department can be deleted only
    /// when no employee is assigned to it and no department is nested under it; otherwise 409. Nothing is ever cascaded or moved.
    /// <para>
    /// Race safety does NOT rest on the counts below (they only give a clear message): the foreign keys Employees.DepartmentId and
    /// Departments.ParentDeptId are RESTRICT, so if an employee is assigned or a child is created after the check, PostgreSQL refuses the
    /// DELETE (or makes the concurrent insert/update fail), the transaction rolls back and the answer is 409.
    /// </para>
    /// </summary>
    [AuditExpectation("department", "department.deleted")]
    public sealed record DeleteDepartmentCommand(Guid DepartmentId) : IRequest, IAuditedRequest;

    public sealed class DeleteDepartmentCommandValidator : AbstractValidator<DeleteDepartmentCommand>
    {
        public DeleteDepartmentCommandValidator() => RuleFor(c => c.DepartmentId).NotEmpty();
    }

    public sealed class DeleteDepartmentCommandHandler : IRequestHandler<DeleteDepartmentCommand>
    {
        public const string InUseMessage = "The department cannot be deleted while employees are assigned to it or departments are nested under it.";

        private readonly IDepartmentRepository _departments;
        private readonly IAuditWriter _audit;

        public DeleteDepartmentCommandHandler(IDepartmentRepository departments, IAuditWriter audit)
        {
            _departments = departments;
            _audit = audit;
        }

        public async Task Handle(DeleteDepartmentCommand command, CancellationToken cancellationToken)
        {
            // Same lock as re-parenting: deletes and hierarchy changes of one company are serialized, so the outcome is deterministic.
            await _departments.LockHierarchyAsync(cancellationToken);

            var department = await _departments.GetForUpdateAsync(command.DepartmentId, cancellationToken)
                ?? throw new NotFoundException(DepartmentRules.NotFoundMessage);

            if (await _departments.CountEmployeesAsync(department.Id, cancellationToken) > 0
                || await _departments.CountChildDepartmentsAsync(department.Id, cancellationToken) > 0)
            {
                throw new ConflictException(InUseMessage);
            }

            _audit.Stage(new AuditEntry("department", "department.deleted", department.Id, DepartmentRules.Snapshot(department), newValue: null));

            _departments.Remove(department);

            try
            {
                await _departments.SaveChangesAsync(cancellationToken);
            }
            catch (ConflictException)
            {
                // The database refused the delete: an employee or child department was committed after the counts above. The
                // surrounding transaction rolls back (audit entry included).
                throw new ConflictException(InUseMessage);
            }
        }
    }
}
