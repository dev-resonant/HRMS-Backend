using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Enums;
using MediatR;

namespace HRMS.Application.Features.Employees.Commands.DeactivateEmployee
{
    /// <summary>
    /// PUT /api/employees/{id}/deactivate (specification 5.2 and the testing checkpoints): sets the last working date, the
    /// status to inactive, blocks the login account and deletes all its sessions - atomically with the audit entry.
    /// The employee record is kept (never deleted). Closing pending Phase 2 requests is not applicable yet.
    /// </summary>
    [AuditExpectation("employee", "employee.deactivated")]
    public sealed record DeactivateEmployeeCommand(Guid EmployeeId, DateOnly LastWorkingDate, IReadOnlyList<uint>? IfMatch = null) : IRequest<DeactivateEmployeeResponse>, IAuditedRequest;

    public sealed class DeactivateEmployeeCommandValidator : AbstractValidator<DeactivateEmployeeCommand>
    {
        public DeactivateEmployeeCommandValidator()
        {
            RuleFor(c => c.EmployeeId).NotEmpty();

            RuleFor(c => c.LastWorkingDate)
                .Must(EmployeeRules.IsPlausibleDate)
                .WithMessage("LastWorkingDate is required and must be between 1900-01-01 and 2100-12-31.");
        }
    }

    public sealed class DeactivateEmployeeCommandHandler : IRequestHandler<DeactivateEmployeeCommand, DeactivateEmployeeResponse>
    {
        private readonly ICurrentUser _currentUser;
        private readonly IEmployeeRepository _employees;
        private readonly IEmployeeQueries _queries;
        private readonly IAuditWriter _audit;

        public DeactivateEmployeeCommandHandler(ICurrentUser currentUser, IEmployeeRepository employees, IEmployeeQueries queries, IAuditWriter audit)
        {
            _currentUser = currentUser;
            _employees = employees;
            _queries = queries;
            _audit = audit;
        }

        public async Task<DeactivateEmployeeResponse> Handle(DeactivateEmployeeCommand command, CancellationToken cancellationToken)
        {
            // The deactivate permission is company-wide (HR Admin); the lookup is bound to the caller's company.
            var employee = await _employees.GetForUpdateAsync(command.EmployeeId, cancellationToken)
                ?? throw new NotFoundException(EmployeeRules.NotFoundMessage);

            EmployeeRules.EnsureVersionMatches(employee, command.IfMatch);

            var user = employee.User;

            if (user.Id == _currentUser.UserId)
            {
                // Assumption (see docs): protects against locking the only administrator out.
                throw new ForbiddenException("You cannot deactivate your own account.");
            }

            if (employee.Status == EmployeeStatus.Inactive && !user.IsActive)
            {
                throw new ConflictException("The employee is already deactivated.");
            }

            if (command.LastWorkingDate < employee.JoiningDate)
            {
                throw EmployeeRules.Invalid("LastWorkingDate", "LastWorkingDate cannot be before JoiningDate.");
            }

            var before = EmployeeRules.Snapshot(employee, user);

            employee.Status = EmployeeStatus.Inactive;
            employee.LastWorkingDate = command.LastWorkingDate;
            user.IsActive = false;                                          // login is blocked from now on

            // Specification 4.1 / 5.2: every device is logged out immediately. Joins the open transaction.
            var sessionsRevoked = await _employees.RevokeSessionsAsync(user.Id, cancellationToken);

            // Reported, not changed: HR must reassign them (decision D10).
            var reports = await _employees.CountActiveDirectReportsAsync(employee.Id, cancellationToken);

            _audit.Stage(new AuditEntry(
                "employee",
                "employee.deactivated",
                employee.Id,
                before,
                EmployeeDeactivationAuditSnapshot.From(EmployeeRules.Snapshot(employee, user), sessionsRevoked, reports)));

            await _employees.SaveChangesAsync(cancellationToken);

            return new DeactivateEmployeeResponse
            {
                Employee = await _queries.GetInCompanyAsync(employee.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The employee that was just deactivated could not be read back."),
                SessionsRevoked = sessionsRevoked,
                DirectReportsNeedingReassignment = reports
            };
        }
    }
}
