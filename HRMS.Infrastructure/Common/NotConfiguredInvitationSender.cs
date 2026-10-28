using HRMS.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Common
{
    /// <summary>
    /// The default invitation channel: none. No email provider, SMTP server or credential is bundled or assumed, so nothing is
    /// delivered and nothing about the invitation (in particular its token) is written anywhere. Replace the registration in
    /// Program.cs with a real implementation of <see cref="IAccountInvitationSender"/> once a provider is chosen (decision D9).
    /// </summary>
    public sealed class NotConfiguredInvitationSender : IAccountInvitationSender
    {
        private readonly ILogger<NotConfiguredInvitationSender> _logger;

        public NotConfiguredInvitationSender(ILogger<NotConfiguredInvitationSender> logger) => _logger = logger;

        public bool IsConfigured => false;

        public Task SendAsync(AccountInvitation invitation, CancellationToken cancellationToken)
        {
            _logger.LogWarning("No invitation delivery channel is configured; the invitation for user {UserId} was not sent.", invitation.UserId);

            return Task.CompletedTask;
        }
    }
}
