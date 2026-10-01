namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// A database's reachability: success is kept, failure is re-asked and carries its reason.
/// </summary>
/// <remarks>
/// A copy of the type beside the Gateway tests, kept separate the way <c>RecordingLogger</c> is
/// kept separate in the two driver test projects — the test projects share no assembly, and a
/// shared project for forty lines would be more machinery than the duplication is worth. Both
/// copies exist for the same measured reason: a first answer that fails is not the same fact as a
/// server that is not there, and caching it for the life of the run tells the next reader the
/// wrong one. A server still starting, or a container whose published port is not bound yet,
/// answers "no" once and "yes" a second later.
/// </remarks>
internal sealed class DatabaseAvailability(Func<(bool Available, string? Reason)> ask)
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private bool _known;
    private bool _available;
    private string? _reason;
    private DateTimeOffset _askedAt;

    internal (bool Available, string? Reason) Ask()
    {
        lock (_gate)
        {
            if (_known && _available)
            {
                return (true, null);
            }

            if (_known && DateTimeOffset.UtcNow - _askedAt < RetryAfter)
            {
                return (false, _reason);
            }

            var (available, reason) = ask();
            _known = true;
            _available = available;
            _reason = reason;
            _askedAt = DateTimeOffset.UtcNow;
            return (available, reason);
        }
    }

    internal bool IsAvailable => Ask().Available;

    /// <summary>Why it is not reachable, in a sentence a skip message can carry.</summary>
    internal string Why => Ask().Reason ?? "No PostgreSQL/TimescaleDB is reachable.";
}
