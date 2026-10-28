using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using MediatR;

namespace HRMS.Application.Features.Departments.Commands.CreateDepartment
{
    /// <summary>
    /// POST /api/departments (specification 5.3 and the API table: HR Admin). Name, optional parent, optional head. The company comes from
    /// the authenticated caller. Atomic with its audit entry.
    /// </summary>
    [AuditExpectation("department", "department.created")]
    public sealed record CreateDepartmentCommand(string Name, Guid? ParentDepartmentId, Guid? HeadEmployeeId) : IRequest<DepartmentDto>, IAuditedRequest;

    public sealed class CreateDepartmentCommandValidator : AbstractValidator<CreateDepartmentCommand>
    {
        public CreateDepartmentCommandValidator()
        {
            RuleFor(c => c.Name).ValidName();
            RuleFor(c => c.ParentDepartmentId).NotEqual(Guid.Empty).When(c => c.ParentDepartmentId.HasValue);
            RuleFor(c => c.HeadEmployeeId).NotEqual(Guid.Empty).When(c => c.HeadEmployeeId.HasValue);
        }
    }

    public sealed class CreateDepartmentCommandHandler : IRequestHandler<CreateDepartmentCommand, DepartmentDto>
    {
        private readonly ICurrentUser _currentUser;
        private readonly IDepartmentRepository _departments;
        private readonly IDepartmentQueries _queries;
        private readonly IEmployeeRepository _employees;
        private readonly IReferenceValidator _references;
        private readonly IAuditWriter _audit;

        public CreateDepartmentCommandHandler(
            ICurrentUser currentUser, IDepartmentRepository departments, IDepartmentQueries queries,
            IEmployeeRepository employees, IReferenceValidator references, IAuditWriter audit)
        {
            _currentUser = currentUser;
            _departments = departments;
            _queries = queries;
            _employees = employees;
            _references = references;
            _audit = audit;
        }

        public async Task<DepartmentDto> Handle(CreateDepartmentCommand command, CancellationToken cancellationToken)
        {
            // A new node cannot close a cycle, so no hierarchy lock is needed; the references are checked against the caller's company.
            if (command.ParentDepartmentId is { } parentId)
            {
                await DepartmentRules.EnsureParentAssignableAsync(_references, _departments, null, parentId, cancellationToken);
            }

            if (command.HeadEmployeeId is { } headId)
            {
                await DepartmentRules.EnsureHeadAssignableAsync(_references, _employees, headId, requireActive: true, cancellationToken);
            }

            var department = new Department
            {
                Id = Guid.NewGuid(),
                CompanyId = _currentUser.CompanyId,                         // trusted: never from the request
                Name = command.Name.Trim(),
                ParentDeptId = command.ParentDepartmentId,
                HeadEmpId = command.HeadEmployeeId
            };

            _departments.Add(department);

            _audit.Stage(new AuditEntry("department", "department.created", department.Id, oldValue: null, newValue: DepartmentRules.Snapshot(department)));

            await _departments.SaveChangesAsync(cancellationToken);

            return await _queries.GetInCompanyAsync(department.Id, cancellationToken)
                ?? throw new InvalidOperationException("The department that was just created could not be read back.");
        }
    }
}
