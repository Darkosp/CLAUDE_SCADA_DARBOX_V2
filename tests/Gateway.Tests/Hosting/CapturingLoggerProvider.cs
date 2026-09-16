using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// Keeps every log entry the Gateway writes — the formatted message and, separately, every
/// structured value — so a test can check that something never reached a log in any form a
/// structured sink could write.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {structured} | {exception}");
        }
    }
}
