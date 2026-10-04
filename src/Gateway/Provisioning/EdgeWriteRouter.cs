using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Routes a tag write to the edge that reads the device, and waits for that edge to say what
/// happened (ADR-0023).
/// </summary>
/// <remarks>
/// <para>
/// The Gateway cannot dial a plant: the link is outbound only (ADR-0017), so a write is published
/// on the connection the edge already holds and the answer arrives on it too. This type is the
/// cloud's half of that conversation — it owns the in-flight writes and is what turns a reply back
/// into the caller's answer.
/// </para>
/// <para>
/// <b>The id is what makes the answer the answer.</b> Several writes may be in flight to one edge
/// and an edge may answer out of order, so every request carries an id and a reply is matched by
/// it. A reply naming an id that is not in flight is ignored rather than interpreted, because
/// attributing a result to the wrong write means believing a plant was set when it was not.
/// </para>
/// <para>
/// <b>A write that was not answered is not a success.</b> The deadline exists so a silent edge ends
/// the call rather than hanging the API, and what it produces is a "not confirmed" — never a
/// written. ADR-0003's rule, applied to a result instead of a value.
/// </para>
/// </remarks>
public interface IEdgeWriteRouter
{
    /// <summary>
    /// Sends one write to the edge that reads <paramref name="device"/>, and waits for its answer.
    /// </summary>
    /// <returns>
    /// True when the edge reported the driver accepted it; false with a reason when the edge
    /// reported a failure, and false with <paramref name="confirmed"/> false when it never answered.
    /// </returns>
    Task<EdgeWriteOutcome> WriteAsync(string edgeName, Guid tagId, TagValue value, CancellationToken cancellationToken);
}

/// <summary>What came of one routed write (ADR-0023).</summary>
/// <param name="Confirmed">Whether the edge answered at all.</param>
/// <param name="Written">True when it answered that the driver accepted the value.</param>
/// <param name="Reason">Why not, when it answered that it failed; null otherwise.</param>
public sealed record EdgeWriteOutcome(bool Confirmed, bool Written, string? Reason);

/// <summary>The in-flight writes, and the matching of a reply back to the call that is waiting.</summary>
public sealed class EdgeWriteRouter : IEdgeWriteRouter
{
    /// <summary>
    /// How long a caller waits for an edge before being told it did not answer (ADR-0023 §4).
    /// </summary>
    /// <remarks>
    /// Five seconds: one MQTT round trip over one link with room for a device that answers slowly,
    /// and short enough that an operator who has asked a plant to do something is not left guessing.
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private readonly EdgeProvisioningOptions _options;
    private readonly ILogger<EdgeWriteRouter> _logger;
    private readonly TimeProvider _clock;

    /// <summary>The calls waiting for an answer, by the id their request carried.</summary>
    private readonly ConcurrentDictionary<Guid, PendingWrite> _pending = new();

    /// <summary>
    /// How a request leaves this process. Set by the publisher, which owns the one MQTT connection
    /// the Gateway holds — the same connection that carries configurations out and declarations in.
    /// </summary>
    private Func<string, string, CancellationToken, Task>? _send;

    public EdgeWriteRouter(
        IOptions<EdgeProvisioningOptions> options,
        ILogger<EdgeWriteRouter> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gives the router the way to publish, once the connection exists. Called by the publisher as
    /// it connects, and again after every reconnect, because a send that captured a dead client
    /// would fail silently for the life of the process.
    /// </summary>
    /// <remarks>
    /// Public so a test can stand in for the broker: the router's job is matching a result back to
    /// the call that is waiting, and a test of that job needs no MQTT connection — it needs a way
    /// to hand the router a message, which is what this is.
    /// </remarks>
    public void UseSender(Func<string, string, CancellationToken, Task> send) => _send = send;

    /// <summary>Whether a deployment lets a write to an edge-assigned device through at all.</summary>
    internal bool Enabled => _options.Enabled && _options.WritesEnabled;

    /// <summary>
    /// Whether the router has a connection to publish on yet. False until the publisher has
    /// connected, and false again if the connection is rebuilt — a caller that asks is told the
    /// truth rather than left to guess at a delay.
    /// </summary>
    public bool CanPublish => _send is not null;

    public async Task<EdgeWriteOutcome> WriteAsync(
        string edgeName,
        Guid tagId,
        TagValue value,
        CancellationToken cancellationToken)
    {
        if (_send is not { } send)
        {
            // Published writes are impossible without the connection, and there is no honest
            // outcome to report other than "it did not happen".
            return new EdgeWriteOutcome(Confirmed: false, Written: false, Reason: null);
        }

        var writeId = Guid.NewGuid();
        var pending = new PendingWrite();
        _pending[writeId] = pending;

        try
        {
            var topic = _options.WritesTopic(edgeName);
            var payload = WritePayload.WriteRequest(writeId, tagId, value);

            try
            {
                await send(topic, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not publish a write to edge {Edge} on {Topic}.",
                    edgeName,
                    topic);
                return new EdgeWriteOutcome(Confirmed: false, Written: false, Reason: null);
            }

            _logger.LogInformation(
                "Asked edge {Edge} to write tag {Tag} on {Topic} (write {Write}).",
                edgeName,
                tagId,
                topic,
                writeId);

            // The deadline is the caller's answer, not a failure of the mechanism: an edge that is
            // silent leaves nothing else to say (ADR-0023 §4).
            var answered = await pending
                .Completion(_clock, Deadline)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (answered is null)
            {
                _logger.LogWarning(
                    "Edge {Edge} did not answer the write of tag {Tag} within {Seconds} s; the write is not confirmed (write {Write}).",
                    edgeName,
                    tagId,
                    (int)Deadline.TotalSeconds,
                    writeId);

                return new EdgeWriteOutcome(Confirmed: false, Written: false, Reason: null);
            }

            return new EdgeWriteOutcome(Confirmed: true, Written: answered.Written, Reason: answered.Reason);
        }
        finally
        {
            // Removed in a finally so a cancelled caller does not leave an entry behind that a
            // later reply would find and complete for nobody.
            _pending.TryRemove(writeId, out _);
        }
    }

    /// <summary>
    /// Reads one result message and completes the call waiting for it. Anything else is reported
    /// and dropped.
    /// </summary>
    public void HandleResult(string payload)
    {
        var result = WritePayload.ReadResult(payload);

        if (result.Refusal is not null)
        {
            _logger.LogWarning("Ignoring a write result that could not be read: {Refusal}", result.Refusal);
            return;
        }

        if (!_pending.TryGetValue(result.WriteId, out var pending))
        {
            // Not an error: the caller may have given up at the deadline a moment before this
            // arrived, and a reply to a write nobody is waiting for changes nothing. Reported so a
            // reader can tell it from silence.
            _logger.LogInformation(
                "A write result arrived for {Write}, which is no longer in flight; it is ignored.",
                result.WriteId);
            return;
        }

        pending.Settle(new Answered(result.Written, result.Reason));
    }

    /// <summary>One call waiting for one answer.</summary>
    private sealed class PendingWrite
    {
        private readonly TaskCompletionSource<Answered?> _answered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The answer, or null when the deadline passed first. Never throws on a second settle: a
        /// duplicate reply to the same id is not a reason to fail the call that already got one.
        /// </summary>
        internal async Task<Answered?> Completion(TimeProvider clock, TimeSpan deadline)
        {
            using var expiry = clock.CreateTimer(
                _ => _answered.TrySetResult(null),
                state: null,
                deadline,
                Timeout.InfiniteTimeSpan);

            return await _answered.Task.ConfigureAwait(false);
        }

        internal void Settle(Answered answered) => _answered.TrySetResult(answered);
    }

    private sealed record Answered(bool Written, string? Reason);
}
