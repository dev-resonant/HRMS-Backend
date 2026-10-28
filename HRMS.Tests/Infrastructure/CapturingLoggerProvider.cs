using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Infrastructure;

/// <summary>Collects log entries written by the hosted API so a test can assert that a diagnostic was (or was not) recorded.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyCollection<Entry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<Entry> _entries;

        public CapturingLogger(string category, ConcurrentQueue<Entry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new Entry(_category, logLevel, formatter(state, exception)));
    }
}
