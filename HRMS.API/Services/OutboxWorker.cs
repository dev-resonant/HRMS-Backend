using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HRMS.API.Services
{
    /// <summary>
    /// Polls the outbox and delivers what is due (retries with backoff, takeover of abandoned claims). Several instances may run at the
    /// same time: the claim in <see cref="InvitationOutboxProcessor"/> keeps them from working on the same message.
    /// </summary>
    public sealed class OutboxWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly OutboxOptions _options;
        private readonly ILogger<OutboxWorker> _logger;

        public OutboxWorker(IServiceScopeFactory scopes, OutboxOptions options, ILogger<OutboxWorker> logger)
        {
            _scopes = scopes;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.WorkerEnabled)
            {
                _logger.LogInformation("The outbox worker is disabled (Outbox:WorkerEnabled = false); only inline delivery attempts are made.");
                return;
            }

            using (var scope = _scopes.CreateScope())
            {
                if (!scope.ServiceProvider.GetRequiredService<IAccountInvitationSender>().IsConfigured)
                {
                    _logger.LogWarning("No invitation delivery channel is configured: invitations stay pending in the outbox until one is.");
                }
            }

            using var timer = new PeriodicTimer(_options.PollInterval);

            do
            {
                try
                {
                    await DrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError("An outbox polling cycle failed with {ExceptionType}; it is retried at the next interval.", ex.GetType().Name);
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }

        /// <summary>Runs batches until nothing more is due.</summary>
        private async Task DrainAsync(CancellationToken cancellationToken)
        {
            int claimed;

            do
            {
                using var scope = _scopes.CreateScope();

                claimed = await scope.ServiceProvider.GetRequiredService<InvitationOutboxProcessor>().ProcessDueAsync(cancellationToken);
            }
            while (claimed >= _options.BatchSize && !cancellationToken.IsCancellationRequested);
        }

        private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                return await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
