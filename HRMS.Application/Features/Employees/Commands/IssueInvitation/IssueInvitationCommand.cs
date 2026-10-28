using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Auth;
using HRMS.Application.Interfaces;
using HRMS.Domain.Enums;
using MediatR;

namespace HRMS.Application.Features.Employees.Commands.IssueInvitation
{
    /// <summary>
    /// POST /api/employees/{id}/invitation (HR Admin). Issues a NEW one-time password-setup invitation for an employee whose account
    /// has not set a password yet, revoking the earlier open one. Used to (re)send the invitation, for example when the first one
    /// expired or no delivery channel existed at creation. The token is never returned.
    /// </summary>
    [AuditExpectation("employee", "employee.invitation_issued")]
    public sealed record IssueInvitationCommand(Guid EmployeeId) : IRequest<InvitationIssuedResponse>, IAuditedRequest;

    public sealed class IssueInvitationCommandValidator : AbstractValidator<IssueInvitationCommand>
    {
        public IssueInvitationCommandValidator() => RuleFor(c => c.EmployeeId).NotEmpty();
    }

    public sealed class IssueInvitationCommandHandler : IRequestHandler<IssueInvitationCommand, InvitationIssuedResponse>
    {
        private readonly IEmployeeRepository _employees;
        private readonly AccountInvitationIssuer _invitations;
        private readonly IAuditWriter _audit;

        public IssueInvitationCommandHandler(IEmployeeRepository employees, AccountInvitationIssuer invitations, IAuditWriter audit)
        {
            _employees = employees;
            _invitations = invitations;
            _audit = audit;
        }

        public async Task<InvitationIssuedResponse> Handle(IssueInvitationCommand command, CancellationToken cancellationToken)
        {
            var employee = await _employees.GetForUpdateAsync(command.EmployeeId, cancellationToken)
                ?? throw new NotFoundException(EmployeeRules.NotFoundMessage);

            var user = employee.User;

            // There is no forgot-password flow in the specification, so an invitation is only for an account that never had its
            // password set: it cannot be used to take over an account that is already in use.
            if (!user.IsActive || !user.IsFirstLogin || employee.Status == EmployeeStatus.Inactive)
            {
                throw new ConflictException("An invitation can only be issued to an active account that has not set its password yet.");
            }

            var issued = await _invitations.IssueAsync(user, cancellationToken);

            // The audited state contains no token or token hash - only who, until when, and what was revoked.
            _audit.Stage(new AuditEntry(
                "employee",
                "employee.invitation_issued",
                employee.Id,
                oldValue: null,
                newValue: new { UserId = user.Id, ExpiresAt = issued.ExpiresAtUtc, PreviousInvitationsRevoked = issued.PreviousInvitationsRevoked }));

            await _employees.SaveChangesAsync(cancellationToken);

            return new InvitationIssuedResponse { ExpiresAt = issued.ExpiresAtUtc, DeliveryConfigured = issued.DeliveryConfigured };
        }
    }
}
