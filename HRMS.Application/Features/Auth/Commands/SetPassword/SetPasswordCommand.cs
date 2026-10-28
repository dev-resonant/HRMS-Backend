using FluentValidation;
using FluentValidation.Results;
using HRMS.Application.Audit;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Auth.Commands.SetPassword
{
    /// <summary>
    /// POST /api/auth/set-password (anonymous; the one-time token is the credential). Completes onboarding: sets the password of the
    /// account the token was issued for, clears the first-login flag and consumes the token, atomically with the audit entry.
    /// The token works only on the tenant host it was issued for, once, and only until it expires.
    /// </summary>
    [AuditExpectation("auth", "auth.password_set")]
    public sealed record SetPasswordCommand(string Token, string NewPassword) : IRequest, IAuditedRequest
    {
        // A record prints every member; the token and the new password must never reach a log, an exception or a debugger string.
        public sealed override string ToString() => nameof(SetPasswordCommand);
    }

    public sealed class SetPasswordCommandValidator : AbstractValidator<SetPasswordCommand>
    {
        public SetPasswordCommandValidator()
        {
            RuleFor(c => c.Token).NotEmpty().MaximumLength(200);

            // Validated before the token is looked up or consumed: a rejected weak password does not burn the invitation.
            RuleFor(c => c.NewPassword).NotEmpty().MeetsPasswordPolicy();
        }
    }

    public sealed class SetPasswordCommandHandler : IRequestHandler<SetPasswordCommand>
    {
        private readonly IAccountRepository _accounts;
        private readonly ITokenHasher _tokenHasher;
        private readonly IPasswordHasher _passwordHasher;
        private readonly TimeProvider _time;
        private readonly ICurrentUserLoader _currentUserLoader;
        private readonly IAuditWriter _audit;
        private readonly ISecurityEventWriter _securityEvents;

        public SetPasswordCommandHandler(
            IAccountRepository accounts, ITokenHasher tokenHasher, IPasswordHasher passwordHasher,
            TimeProvider time, ICurrentUserLoader currentUserLoader, IAuditWriter audit, ISecurityEventWriter securityEvents)
        {
            _accounts = accounts;
            _tokenHasher = tokenHasher;
            _passwordHasher = passwordHasher;
            _time = time;
            _currentUserLoader = currentUserLoader;
            _audit = audit;
            _securityEvents = securityEvents;
        }

        // One answer for every failure (unknown, expired, used, revoked, wrong tenant, deactivated account): no oracle for guessing.
        private static ValidationException InvalidLink()
            => new(new[] { new ValidationFailure("Token", "The password-setup link is invalid or has expired.") });

        /// <summary>
        /// Records the rejected attempt (own connection, so it outlives the rollback of this command) and returns the one generic answer.
        /// The account is named only when trusted server-side state proves it (the token resolved to it); an unknown token names nobody.
        /// What is recorded is the reason code and the client address - never the token, the submitted password or any hash.
        /// </summary>
        private async Task<ValidationException> RejectAsync(string reason, Guid? userId, CancellationToken cancellationToken)
        {
            await _securityEvents.RecordAsync(new SecurityEventEntry(SecurityEventTypes.SetPasswordFailed, reason, userId), cancellationToken);

            return InvalidLink();
        }

        public async Task Handle(SetPasswordCommand command, CancellationToken cancellationToken)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var token = await _accounts.FindUsableTokenAsync(_tokenHasher.Hash(command.Token.Trim()), now, cancellationToken)
                ?? throw await RejectAsync(SecurityEventReasons.InvalidToken, null, cancellationToken);

            var user = await _accounts.GetUserForUpdateAsync(token.UserId, cancellationToken);

            if (user is null || !user.IsActive || !user.IsFirstLogin)
            {
                throw await RejectAsync(SecurityEventReasons.AccountNotEligible, user?.Id, cancellationToken);
            }

            // Single use: a conditional UPDATE, so of two simultaneous requests with the same token exactly one succeeds.
            if (!await _accounts.TryConsumeTokenAsync(token.Id, now, cancellationToken))
            {
                throw await RejectAsync(SecurityEventReasons.InvalidToken, user.Id, cancellationToken);
            }

            // The token proved who the actor is, so the account becomes the current user for the audit entry (actor, company, IP).
            if (await _currentUserLoader.LoadAsync(user.Id, user.CompanyId, cancellationToken) != CurrentUserLoadResult.Loaded)
            {
                throw await RejectAsync(SecurityEventReasons.AccountNotEligible, user.Id, cancellationToken);
            }

            user.PasswordHash = _passwordHasher.Hash(command.NewPassword);
            user.IsFirstLogin = false;

            // No login should exist yet, but anything that does is ended.
            var sessionsRevoked = await _accounts.RevokeSessionsAsync(user.Id, null, cancellationToken);

            _audit.Stage(new AuditEntry(
                "auth",
                "auth.password_set",
                user.Id,
                new { FirstLoginPending = true },
                new { FirstLoginPending = false, SessionsRevoked = sessionsRevoked }));

            await _accounts.SaveChangesAsync(cancellationToken);
        }
    }
}
