using System.ComponentModel;
using System.Diagnostics;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// What happened when a test asked the machine whether something it needs is there.
/// </summary>
/// <remarks>
/// A skipped test is evidence of nothing, so the whole value of a skip is the reason it gives.
/// Before this existed every failure was one cached <c>false</c>, and a command that did not
/// answer within fifteen seconds was reported to the next reader as "Docker is not available" —
/// a statement about the machine, and a false one. Measured on 2026-10-01: with <c>docker
/// version</c> answering <c>29.8.0</c>, a whole-solution run still reported the broker tests as
/// skipped, while the same tests run on their own passed nine of nine.
/// </remarks>
internal enum ProbeOutcome
{
    /// <summary>It answered, and said yes.</summary>
    Available,

    /// <summary>It answered and said no, or there is no such tool to run.</summary>
    Absent,

    /// <summary>It did not answer within the time allowed — a fact about the moment, not the machine.</summary>
    TimedOut,

    /// <summary>It could not be started at all: the process and its pipes were refused.</summary>
    Blocked,
}

/// <summary>Asks a command-line tool whether it is there, and reports how it answered.</summary>
internal static class ToolProbe
{
    /// <summary>
    /// Runs the tool and waits at most <paramref name="bound"/> for it.
    /// </summary>
    /// <remarks>
    /// The output is redirected because it is never read and would otherwise be printed into the
    /// test log; that is also why a sandbox which denies a redirected process answers
    /// <see cref="ProbeOutcome.Blocked"/> rather than a verdict about the tool.
    /// </remarks>
    internal static ProbeOutcome Run(string fileName, string arguments, TimeSpan bound)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return ProbeOutcome.Absent;
            }

            if (!process.WaitForExit(bound))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // It finished between the wait giving up and the kill; nothing to do.
                }

                return ProbeOutcome.TimedOut;
            }

            return process.ExitCode == 0 ? ProbeOutcome.Available : ProbeOutcome.Absent;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            // ERROR_FILE_NOT_FOUND: there is no such tool on this machine.
            return ProbeOutcome.Absent;
        }
        catch (Exception)
        {
            // It exists but could not be run here — a denied pipe is the case this project has met.
            return ProbeOutcome.Blocked;
        }
    }

    /// <summary>A sentence for a skip message, naming which of the four happened.</summary>
    internal static string Describe(ProbeOutcome outcome, string what, TimeSpan bound) => outcome switch
    {
        ProbeOutcome.Available => $"{what} is available.",
        ProbeOutcome.Absent => $"{what} is not installed on this machine.",
        ProbeOutcome.TimedOut =>
            $"{what} did not answer within {bound.TotalSeconds:0} seconds; that is not the same as it being absent.",
        _ => $"{what} could not be started here (a process whose output is redirected was refused).",
    };
}

/// <summary>
/// A tool's availability, remembered only when the answer is a fact about the machine.
/// </summary>
/// <remarks>
/// <see cref="ProbeOutcome.Available"/> and <see cref="ProbeOutcome.Absent"/> will not change
/// while the run lasts, so they are asked once and kept. A timeout or a refusal may well change —
/// the machine was busy, the daemon was still starting — so those are asked again rather than
/// turned into a verdict that outlives the moment. That difference is the whole reason this type
/// exists instead of a <c>Lazy&lt;bool&gt;</c>.
/// </remarks>
internal sealed class ToolAvailability(string what, TimeSpan bound, Func<ProbeOutcome> ask)
{
    private readonly object _gate = new();
    private ProbeOutcome? _settled;

    /// <summary>How the tool answered, asking again if the last answer was not a settled one.</summary>
    internal ProbeOutcome Outcome
    {
        get
        {
            lock (_gate)
            {
                if (_settled is { } known)
                {
                    return known;
                }

                var outcome = ask();
                if (outcome is ProbeOutcome.Available or ProbeOutcome.Absent)
                {
                    _settled = outcome;
                }

                return outcome;
            }
        }
    }

    internal bool IsAvailable => Outcome == ProbeOutcome.Available;

    /// <summary>Why it is not available, in a sentence a skip message can carry.</summary>
    internal string Why => ToolProbe.Describe(Outcome, what, bound);
}

/// <summary>
/// A database's reachability: success is kept, failure is re-asked and carries its reason.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="ToolAvailability"/>, one step further. A server that is still
/// starting, or a container whose published port is not bound yet, answers "no" once and "yes" a
/// second later; caching that first "no" for the life of the run tells the next reader the machine
/// has no database, which is the shape of lie this project keeps finding elsewhere. The reason is
/// carried out because "nothing answered at localhost:5432" and "the password was refused" are
/// different problems with different fixes.
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

/// <summary>
/// A fact that needs a process started with its output redirected — which a sandbox may refuse.
/// </summary>
/// <remarks>
/// The probe tests are the one place in the suite that cannot work at all where process creation
/// is denied, because what they assert is how a process answers. Skipping them, with that reason,
/// is the honest outcome; asserting through it is not possible.
/// </remarks>
public sealed class RequiresProcessSpawningFactAttribute : FactAttribute
{
    private static readonly ToolAvailability Spawning = new(
        "Starting a process with its output redirected",
        TimeSpan.FromSeconds(20),
        () => ToolProbe.Run("dotnet", "--version", TimeSpan.FromSeconds(20)));

    public RequiresProcessSpawningFactAttribute()
    {
        if (!Spawning.IsAvailable)
        {
            Skip = Spawning.Why;
        }
    }
}
