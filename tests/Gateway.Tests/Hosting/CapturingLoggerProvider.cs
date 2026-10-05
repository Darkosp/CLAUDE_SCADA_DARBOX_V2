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

    /// <summary>
    /// The logger a component typed to take <c>ILogger&lt;T&gt;</c> accepts.
    /// </summary>
    /// <remarks>
    /// The framework's own extension cannot be used here, because it starts from an
    /// <c>ILoggerFactory</c> and this type is only the provider that factory would hold.
    /// </remarks>
    public ILogger<T> CreateLogger<T>() => new TypedCapturingLogger<T>(CreateLogger(typeof(T).FullName ?? typeof(T).Name));

    private sealed class TypedCapturingLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

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
