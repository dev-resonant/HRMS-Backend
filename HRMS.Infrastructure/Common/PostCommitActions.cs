using HRMS.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Common
{
    /// <summary>
    /// Request-scoped queue of work that runs only after the outermost transaction has committed (see <see cref="IPostCommitActions"/>).
    /// <see cref="Clear"/> drops it when the transaction rolls back. A failing action is logged by name and exception type only (the
    /// message could contain a recipient or a secret) and never affects the committed operation or the other actions.
    /// </summary>
    public sealed class PostCommitActions : IPostCommitActions
    {
        private readonly List<(string Name, Func<CancellationToken, Task> Action)> _actions = new();
        private readonly ILogger<PostCommitActions> _logger;

        public PostCommitActions(ILogger<PostCommitActions> logger) => _logger = logger;

        public int Pending => _actions.Count;

        public void Enqueue(string name, Func<CancellationToken, Task> action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(action);

            _actions.Add((name, action));
        }

        public void Clear() => _actions.Clear();

        public async Task RunAsync()
        {
            var actions = _actions.ToArray();
            _actions.Clear();

            foreach (var (name, action) in actions)
            {
                try
                {
                    // Not cancellable: the data is committed, so the follow-up work must not be cut short by a disconnected client.
                    await action(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError("The post-commit action {Action} failed with {ExceptionType}; the committed operation is unaffected.", name, ex.GetType().Name);
                }
            }
        }
    }
}
