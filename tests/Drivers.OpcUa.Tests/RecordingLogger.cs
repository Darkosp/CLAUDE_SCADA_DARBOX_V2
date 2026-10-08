using Microsoft.Extensions.Logging;

namespace ScadaDarbox.Drivers.OpcUa.Tests;

/// <summary>
/// Keeps what a driver wrote, so a test can assert on the reason a reading does not carry —
/// the log is the only place that reason exists.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<Entry> _entries = [];

    internal IReadOnlyList<Entry> Entries => _entries;

    internal IReadOnlyList<Entry> At(LogLevel level) =>
        [.. _entries.Where(entry => entry.Level == level)];

    /// <summary>Forgets everything recorded so far.</summary>
    /// <remarks>
    /// **For separating what a connect writes from what a scan writes.** Since ADR-0033 a connect can
    /// legitimately warn — an unsecured session was asked for, or an untrusted certificate was
    /// accepted — and a test whose subject is the scan would otherwise be reading lines about the
    /// handshake. Clearing after connecting keeps each test's subject its own.
    /// </remarks>
    internal void Clear() => _entries.Clear();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    // Nothing a driver writes is a level a test wants suppressed; the point is to see all of it.
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add(new Entry(logLevel, formatter(state, exception), exception));

    /// <param name="Message">The formatted message, as a console sink would print it.</param>
    /// <param name="Exception">What was passed alongside it, when there was one.</param>
    internal sealed record Entry(LogLevel Level, string Message, Exception? Exception);
}
