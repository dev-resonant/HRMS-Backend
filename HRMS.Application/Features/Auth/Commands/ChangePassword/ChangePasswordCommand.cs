using FluentValidation;
using FluentValidation.Results;
using HRMS.Application.Audit;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Auth.Commands.ChangePassword
{
    /// <summary>
    /// POST /api/auth/change-password (specification checklist: "enforce on first login"). The caller proves knowledge of the current
    /// password, sets a new one and clears the first-login flag. <see cref="CurrentToken"/> is the bearer token of THIS request (set
    /// by the controller, never by the client): its session is kept, every other session of the account is deleted.
    /// </summary>
    [AuditExpectation("auth", "auth.password_changed")]
    public sealed record ChangePasswordCommand(string CurrentToken, string CurrentPassword, string NewPassword) : IRequest, IAuditedRequest
    {
        // A record prints every member, including the bearer token and both passwords; none of them may reach a log or an exception.
        public sealed override string ToString() => nameof(ChangePasswordCommand);
    }

    public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
    {
        public ChangePasswordCommandValidator()
        {
            RuleFor(c => c.CurrentPassword).NotEmpty().MaximumLength(128);

            RuleFor(c => c.NewPassword).NotEmpty().MeetsPasswordPolicy();

            RuleFor(c => c.NewPassword)
                .Must((c, newPassword) => !string.Equals(newPassword, c.CurrentPassword, StringComparison.Ordinal))
                .WithMessage("NewPassword must be different from the current password.");
        }
    }

    public sealed class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand>
    {
        private readonly ICurrentUser _currentUser;
        private readonly IAccountRepository _accounts;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITokenHasher _tokenHasher;
        private readonly IAuditWriter _audit;
        private readonly ISecurityEventWriter _securityEvents;

        public ChangePasswordCommandHandler(
            ICurrentUser currentUser, IAccountRepository accounts, IPasswordHasher passwordHasher, ITokenHasher tokenHasher,
            IAuditWriter audit, ISecurityEventWriter securityEvents)
        {
            _currentUser = currentUser;
            _accounts = accounts;
            _passwordHasher = passwordHasher;
            _tokenHasher = tokenHasher;
            _audit = audit;
            _securityEvents = securityEvents;
        }

        public async Task Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
        {
            var user = await _accounts.GetUserForUpdateAsync(_currentUser.UserId, cancellationToken)
                ?? throw new UnauthorizedAccessException("User not found.");

            if (!_passwordHasher.Verify(command.CurrentPassword, user.PasswordHash))
            {
                // The caller is authenticated, so the account is known for certain. Recorded on its own connection (it must survive the
                // rollback below); the wrong password itself is never recorded.
                await _securityEvents.RecordAsync(
                    new SecurityEventEntry(SecurityEventTypes.ChangePasswordFailed, SecurityEventReasons.WrongCurrentPassword, user.Id),
                    cancellationToken);

                throw new ValidationException(new[] { new ValidationFailure("CurrentPassword", "CurrentPassword is incorrect.") });
            }

            if (string.Equals(command.NewPassword, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException(new[] { new ValidationFailure("NewPassword", "NewPassword must not be your email address.") });
            }

            var firstLoginWasPending = user.IsFirstLogin;

            user.PasswordHash = _passwordHasher.Hash(command.NewPassword);
            user.IsFirstLogin = false;

            // A changed password ends every other login of the account (for example one an attacker holds); this device stays signed in.
            var otherSessionsRevoked = await _accounts.RevokeSessionsAsync(user.Id, _tokenHasher.Hash(command.CurrentToken), cancellationToken);

            // The audited states describe the account's security status only - never the password, its hash or any token.
            _audit.Stage(new AuditEntry(
                "auth",
                "auth.password_changed",
                user.Id,
                new { FirstLoginPending = firstLoginWasPending },
                new { FirstLoginPending = false, OtherSessionsRevoked = otherSessionsRevoked }));

            await _accounts.SaveChangesAsync(cancellationToken);
        }
    }
}
