using Microsoft.Extensions.Logging;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Configuration;

/// <summary>
/// The devices this edge is reading, and the revision of the configuration they came from
/// (ADR-0019 §5). Acquisition reads it, the link writes it, and nothing else holds a device list:
/// the file the edge is started with is no longer a second place a tag id can be typed.
/// </summary>
/// <remarks>
/// <para>
/// It starts as whatever this edge last accepted, read back from the buffer, so a restart taken
/// while the link is down leaves the edge reading what it was reading. A revision already in force
/// is not a change: the broker hands the retained message to every reconnect, and restarting
/// acquisition for a configuration identical to the one running would cost a scan for nothing.
/// </para>
/// <para>
/// <see cref="Changed"/> is how acquisition learns to restart. It returns a task for the *next*
/// change, and a caller captures it *before* reading <see cref="Devices"/>: a configuration that
/// lands while those loops are being started completes the captured task rather than being missed,
/// which a wait started afterwards would.
/// </para>
/// </remarks>
public sealed class EdgeConfigurationSource
{
    private readonly Lock _gate = new();

    private IReadOnlyList<EdgeConfigurationDevice> _devices = [];
    private string? _revision;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The devices to read now; empty until this edge has accepted a configuration.</summary>
    public IReadOnlyList<EdgeConfigurationDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    /// <summary>The revision in force, or null when nothing has been accepted yet.</summary>
    public string? Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="devices"/> in force as revision <paramref name="revision"/>; false when
    /// that revision is the one already in force, in which case nothing changes and nothing restarts.
    /// </summary>
    public bool Replace(IReadOnlyList<EdgeConfigurationDevice> devices, string revision)
    {
        TaskCompletionSource replaced;

        lock (_gate)
        {
            if (string.Equals(_revision, revision, StringComparison.Ordinal))
            {
                return false;
            }

            _devices = devices;
            _revision = revision;
            replaced = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        replaced.SetResult();
        return true;
    }

    /// <summary>A task that completes the next time the configuration changes.</summary>
    public Task Changed()
    {
        lock (_gate)
        {
            return _changed.Task;
        }
    }

    /// <summary>
    /// The source an edge starts with: the configuration it last accepted, read back from its own
    /// buffer (ADR-0019 §5).
    /// </summary>
    /// <remarks>
    /// A stored message this build cannot read is reported and skipped, and the edge reads nothing
    /// until the cloud sends one. The alternative — starting on whatever can be salvaged — would put
    /// an edge into service reading devices nobody asked it to read.
    /// </remarks>
    public static EdgeConfigurationSource From(SampleBuffer buffer, ILogger logger)
    {
        var source = new EdgeConfigurationSource();

        if (buffer.AcceptedConfiguration() is not { } accepted)
        {
            logger.LogInformation("No configuration has been accepted yet; this edge reads nothing until the cloud sends one.");
            return source;
        }

        var read = EdgeConfigurationPayload.Read(accepted.Payload);
        if (read.Refusal is { } refusal)
        {
            logger.LogError(
                "The configuration accepted at {ReceivedAtUtc:O} ({Revision}) cannot be read ({Refusal}); this edge reads nothing until the cloud sends one.",
                accepted.ReceivedAtUtc,
                accepted.Revision,
                refusal);
            return source;
        }

        source.Replace(read.Devices, read.Revision!);

        logger.LogInformation(
            "Started on the configuration accepted at {ReceivedAtUtc:O}: {Revision}, {Devices} device(s).",
            accepted.ReceivedAtUtc,
            read.Revision,
            read.Devices.Count);

        return source;
    }
}
