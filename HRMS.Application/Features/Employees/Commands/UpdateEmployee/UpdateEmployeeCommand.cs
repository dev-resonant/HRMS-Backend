using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Enums;
using MediatR;

namespace HRMS.Application.Features.Employees.Commands.UpdateEmployee
{
    /// <summary>
    /// PUT /api/employees/{id}. Replaces the editable profile fields. Email, employee code, company, role and the account's
    /// password cannot be changed here. The status cannot become inactive here (use deactivate).
    /// </summary>
    [AuditExpectation("employee", "employee.updated")]
    public sealed record UpdateEmployeeCommand(
        Guid EmployeeId,
        string Name,
        Guid DepartmentId,
        Guid? ManagerId,
        string Designation,
        DateOnly JoiningDate,
        string Status,
        DateOnly? LastWorkingDate,
        string? PersonalEmail = null,
        string? Phone = null,
        string? EmploymentType = null,
        IReadOnlyList<uint>? IfMatch = null) : IRequest<EmployeeDto>, IAuditedRequest;

    public sealed class UpdateEmployeeCommandValidator : AbstractValidator<UpdateEmployeeCommand>
    {
        public UpdateEmployeeCommandValidator()
        {
            RuleFor(c => c.EmployeeId).NotEmpty();

            RuleFor(c => c.Name).NotEmpty().MaximumLength(EmployeeRules.MaxNameLength).NoNulCharacters();

            RuleFor(c => c.DepartmentId).NotEmpty();

            RuleFor(c => c.ManagerId).NotEqual(Guid.Empty).When(c => c.ManagerId.HasValue);

            RuleFor(c => c.ManagerId).NotEqual(c => c.EmployeeId).When(c => c.ManagerId.HasValue)
                .WithMessage("An employee cannot be their own manager.");

            RuleFor(c => c.Designation).NotEmpty().MaximumLength(EmployeeRules.MaxDesignationLength).NoNulCharacters();

            RuleFor(c => c.JoiningDate)
                .Must(EmployeeRules.IsPlausibleDate)
                .WithMessage("JoiningDate is required and must be between 1900-01-01 and 2100-12-31.");

            RuleFor(c => c.Status)
                .NotEmpty()
                .Must(s => EmployeeStatusNames.TryParse(s, out var status) && status != EmployeeStatus.Inactive)
                .WithMessage("Status must be one of: active, probation, notice_period, contract. Use the deactivate endpoint to make an employee inactive.");

            // Assumption (see docs): a last working date belongs to an employee who is leaving.
            RuleFor(c => c.LastWorkingDate)
                .NotNull().WithMessage("LastWorkingDate is required when the status is notice_period.")
                .When(c => EmployeeStatusNames.TryParse(c.Status, out var s) && s == EmployeeStatus.NoticePeriod);

            RuleFor(c => c.LastWorkingDate)
                .Null().WithMessage("LastWorkingDate can only be set when the status is notice_period (or through deactivation).")
                .When(c => EmployeeStatusNames.TryParse(c.Status, out var s) && s is EmployeeStatus.Active or EmployeeStatus.Probation or EmployeeStatus.Contract);

            RuleFor(c => c.LastWorkingDate)
                .Must(d => d is null || EmployeeRules.IsPlausibleDate(d.Value))
                .WithMessage("LastWorkingDate must be between 1900-01-01 and 2100-12-31.");

            RuleFor(c => c.LastWorkingDate)
                .Must((c, d) => d is null || d.Value >= c.JoiningDate)
                .WithMessage("LastWorkingDate cannot be before JoiningDate.");

            this.AddContactRules(c => c.PersonalEmail, c => c.Phone, c => c.EmploymentType);
        }
    }

    public sealed class UpdateEmployeeCommandHandler : IRequestHandler<UpdateEmployeeCommand, EmployeeDto>
    {
        private readonly IEmployeeRepository _employees;
        private readonly IEmployeeQueries _queries;
        private readonly IDataScopeService _scope;
        private readonly IReferenceValidator _references;
        private readonly IAuditWriter _audit;

        public UpdateEmployeeCommandHandler(
            IEmployeeRepository employees,
            IEmployeeQueries queries,
            IDataScopeService scope,
            IReferenceValidator references,
            IAuditWriter audit)
        {
            _employees = employees;
            _queries = queries;
            _scope = scope;
            _references = references;
            _audit = audit;
        }

        public async Task<EmployeeDto> Handle(UpdateEmployeeCommand command, CancellationToken cancellationToken)
        {
            // Company-bound load + write-scope check; a missing, foreign or out-of-scope employee is the same 404.
            var employee = await _employees.GetForUpdateAsync(command.EmployeeId, cancellationToken);

            if (employee is null || !await _scope.CanWriteEmployeeAsync(employee.Id, cancellationToken))
            {
                throw new NotFoundException(EmployeeRules.NotFoundMessage);
            }

            // Optional If-Match: a form that was loaded before someone else changed the employee must not overwrite that change.
            EmployeeRules.EnsureVersionMatches(employee, command.IfMatch);

            if (employee.Status == EmployeeStatus.Inactive)
            {
                // Reactivation is not specified, so a deactivated employee is read-only.
                throw new ConflictException("A deactivated employee cannot be modified.");
            }

            await _references.EnsureDepartmentInCompanyAsync(command.DepartmentId, "DepartmentId", cancellationToken);

            if (command.ManagerId is { } managerId)
            {
                var managerChanges = managerId != employee.ManagerId;

                await EmployeeRules.EnsureManagerAssignableAsync(_references, _employees, managerId, cancellationToken, requireActive: managerChanges);

                if (managerChanges && await _employees.WouldCreateReportingCycleAsync(employee.Id, managerId, cancellationToken))
                {
                    throw EmployeeRules.Invalid("ManagerId", "ManagerId would create a circular reporting line.");
                }
            }

            EmployeeStatusNames.TryParse(command.Status, out var status);        // validated by the validator

            var user = employee.User;
            var before = EmployeeRules.Snapshot(employee, user);

            user.Name = command.Name.Trim();
            employee.DepartmentId = command.DepartmentId;
            employee.ManagerId = command.ManagerId;
            employee.Designation = command.Designation.Trim();
            employee.JoiningDate = command.JoiningDate;
            employee.Status = status;
            employee.LastWorkingDate = command.LastWorkingDate;
            employee.PersonalEmail = EmployeeRules.CleanOptionalEmail(command.PersonalEmail);
            employee.Phone = EmployeeRules.CleanOptional(command.Phone);
            employee.EmploymentType = EmployeeRules.ParseEmploymentType(command.EmploymentType);

            // Always write the employee row (even for a name-only edit, which changes the account row): its version then advances on
            // every update, so the concurrency token and the If-Match precondition cover every edit made through this endpoint.
            _employees.MarkChanged(employee);

            // A new joining date could now sit after an existing last working date.
            if (employee.LastWorkingDate is { } last && last < employee.JoiningDate)
            {
                throw EmployeeRules.Invalid("LastWorkingDate", "LastWorkingDate cannot be before JoiningDate.");
            }

            _audit.Stage(new AuditEntry("employee", "employee.updated", employee.Id, before, EmployeeRules.Snapshot(employee, user)));

            await _employees.SaveChangesAsync(cancellationToken);

            return await _queries.GetInCompanyAsync(employee.Id, cancellationToken)
                ?? throw new InvalidOperationException("The employee that was just updated could not be read back.");
        }
    }
}
