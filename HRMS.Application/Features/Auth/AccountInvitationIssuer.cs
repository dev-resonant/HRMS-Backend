using System.Security.Cryptography;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace HRMS.Application.Features.Auth
{
    public sealed record IssuedInvitation(DateTime ExpiresAtUtc, int PreviousInvitationsRevoked, bool DeliveryConfigured);

    /// <summary>
    /// Creates a one-time password-setup token and its outbox message for an account INSIDE the caller's transaction. Delivery happens
    /// after the commit, outside any transaction, and is retried by the outbox worker. Only the SHA-256 hash of a token is stored. Issuing a new invitation revokes the open ones, so at most one token
    /// per account is usable at any time.
    /// </summary>
    public sealed class AccountInvitationIssuer
    {
        private const int TokenBytes = 32;                       // 256 bits of randomness

        private readonly IAccountRepository _accounts;
        private readonly ITokenHasher _tokenHasher;
        private readonly TimeProvider _time;
        private readonly AccountSetupOptions _options;
        private readonly IPostCommitActions _postCommit;
        private readonly IAccountInvitationSender _sender;
        private readonly IInvitationDispatcher _dispatcher;
        private readonly OutboxOptions _outbox;
        private readonly ITenantContext _tenant;
        private readonly ILogger<AccountInvitationIssuer> _logger;

        public AccountInvitationIssuer(
            IAccountRepository accounts,
            ITokenHasher tokenHasher,
            TimeProvider time,
            AccountSetupOptions options,
            IPostCommitActions postCommit,
            IAccountInvitationSender sender,
            IInvitationDispatcher dispatcher,
            OutboxOptions outbox,
            ITenantContext tenant,
            ILogger<AccountInvitationIssuer> logger)
        {
            _accounts = accounts;
            _tokenHasher = tokenHasher;
            _time = time;
            _options = options;
            _postCommit = postCommit;
            _sender = sender;
            _dispatcher = dispatcher;
            _outbox = outbox;
            _tenant = tenant;
            _logger = logger;
        }

        public async Task<IssuedInvitation> IssueAsync(User user, CancellationToken cancellationToken)
        {
            if (!_tenant.IsResolved || _tenant.CompanyId != user.CompanyId)
            {
                throw new InvalidOperationException("An invitation can only be issued for an account of the resolved tenant.");
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var expiresAt = now + _options.InvitationLifetime;

            var revoked = await _accounts.RevokeOpenTokensAsync(user.Id, now, cancellationToken);

            // The raw value is generated only to obtain a unique, unguessable placeholder hash and is then discarded: nobody ever holds
            // this token. The delivery worker replaces the hash with the hash of the token it actually sends.
            var tokenId = Guid.NewGuid();

            _accounts.AddToken(new AccountSetupToken
            {
                Id = tokenId,
                CompanyId = user.CompanyId,
                UserId = user.Id,
                TokenHash = _tokenHasher.Hash(NewToken()),
                ExpiresAt = expiresAt,
                CreatedAt = now
            });

            // The durable delivery intent, saved or rolled back together with the account and the token. It holds no secret.
            var message = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                CompanyId = user.CompanyId,
                UserId = user.Id,
                SetupTokenId = tokenId,
                Kind = OutboxMessage.AccountInvitationKind,
                Status = OutboxStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now
            };

            _accounts.AddOutboxMessage(message);

            // After the commit, ask for an immediate attempt. If this never runs (crash, no worker) the persisted message is still
            // picked up by the background worker; if the transaction rolls back this action is discarded with it.
            if (_outbox.InlineDelivery)
            {
                _postCommit.Enqueue("account-invitation", ct => _dispatcher.NudgeAsync(message.Id, ct));
            }

            if (!_sender.IsConfigured)
            {
                _logger.LogWarning(
                    "User {UserId} was invited but no invitation delivery channel is configured, so the message stays pending in the outbox. " +
                    "It is delivered once a channel exists, if the invitation has not expired by then; otherwise re-issue it.", user.Id);
            }

            return new IssuedInvitation(expiresAt, revoked, _sender.IsConfigured);
        }

        /// <summary>URL-safe random token: 32 random bytes, base64url without padding (43 characters).</summary>
        public static string NewToken()
            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
