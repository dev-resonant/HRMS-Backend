using HRMS.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Outbox
{
    /// <summary>
    /// The inline "try it right now" path: runs the outbox processor for one message in a scope of its own (a fresh database context and
    /// connection, never the committed request's). Whatever happens here, the message is already durable and the worker will retry it.
    /// </summary>
    public sealed class InvitationDispatcher : IInvitationDispatcher
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<InvitationDispatcher> _logger;

        public InvitationDispatcher(IServiceScopeFactory scopes, ILogger<InvitationDispatcher> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        public async Task NudgeAsync(Guid messageId, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopes.CreateScope();

                await scope.ServiceProvider.GetRequiredService<InvitationOutboxProcessor>().ProcessOneAsync(messageId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("The immediate delivery attempt for outbox message {MessageId} failed with {ExceptionType}; the worker retries it.", messageId, ex.GetType().Name);
            }
        }
    }
}
