using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Drivers;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// A pushing driver over MQTT (ADR-0016, ADR-0017): subscribes to one topic filter and hands
/// every sample it receives to Core, carrying the source's own timestamp and quality.
/// </summary>
/// <remarks>
/// <para>
/// It never answers "what is the value now". While the broker is unreachable it hands nothing
/// over and keeps trying to reconnect; Core's staleness rule is what turns that silence into Bad.
/// </para>
/// <para>
/// "Acknowledged" means stored (ADR-0017). The session is persistent — MQTT 5, a fixed client id,
/// <c>clean start = false</c> — so the broker holds what is published while this Gateway is away
/// and delivers it when it is back. And a message is acknowledged only once everything in it has
/// been handed over without failing. One that could not be stored — the database unreachable, say
/// — is left unacknowledged, and the connection is dropped so that the broker delivers it again on
/// the resumed session. Ingestion is idempotent (ADR-0017), so a message that was half stored the
/// first time is stored once, not twice.
/// </para>
/// </remarks>
public sealed class MqttPushingDriver : IPushingDeviceDriver
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionCheck = TimeSpan.FromMilliseconds(500);

    private readonly MqttConnection _connection;
    private readonly ILogger _logger;
    private readonly IMqttClient _client;

    // Set by the message handler when a message could not be stored; the run loop then drops the
    // connection so the broker delivers it again.
    private volatile bool _redeliver;

    internal MqttPushingDriver(MqttConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
    }

    public TimeSpan StalenessLimit => _connection.StalenessLimit;

    public async Task RunAsync(IReadOnlyList<DriverTag> tags, IPushedSampleSink sink, CancellationToken cancellationToken)
    {
        var byId = tags.ToDictionary(tag => tag.TagId);

        async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs message)
        {
            // Acknowledged below, and only if what it carries was stored.
            message.AutoAcknowledge = false;

            var topic = message.ApplicationMessage.Topic;
            if (await StoreAsync(message.ApplicationMessage, byId, sink, cancellationToken).ConfigureAwait(false))
            {
                await message.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "A message on {Topic} could not be stored; it is not acknowledged, and the broker will deliver it again.",
                    topic);
                _redeliver = true;
            }
        }

        // Added for this run and removed when it ends: the scanner runs a driver again after a
        // failure, and a handler left from the last run would hand every message over twice.
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_connection.Host, _connection.Port)
            .WithClientId(_connection.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            // A persistent session: the broker keeps the subscription and queues what arrives for
            // it while this client is away, for up to the session expiry.
            .WithCleanStart(false)
            .WithSessionExpiryInterval(_connection.SessionExpirySeconds);

        if (_connection.Tls is { } tls)
        {
            builder = builder.WithMutualTls(tls);
        }

        var options = builder.Build();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_redeliver && _client.IsConnected)
                {
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
                    await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                }

                if (!_client.IsConnected)
                {
                    try
                    {
                        _redeliver = false;
                        var connected = await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                        await _client.SubscribeAsync(
                            new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(_connection.Topic, MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build(),
                            cancellationToken).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Subscribed to {Topic} on {Host}:{Port} ({Session}).",
                            _connection.Topic,
                            _connection.Host,
                            _connection.Port,
                            connected.IsSessionPresent ? "session resumed" : "new session");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Not rethrown: an unreachable broker is an ordinary condition, and the
                        // tags' silence already says so (ADR-0016).
                        _logger.LogWarning(
                            "Cannot reach the MQTT broker at {Host}:{Port}: {Reason}", _connection.Host, _connection.Port, exception.Message);
                        await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                await Task.Delay(ConnectionCheck, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped. Anything received and not yet acknowledged is delivered again next time.
        }
        finally
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectQuietlyAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    /// <summary>
    /// Hands one message over. True when there is nothing more to do with it: everything in it
    /// was stored, or it can never be stored — unreadable, or for tags this device does not have —
    /// and delivering it again would change nothing.
    /// </summary>
    private async Task<bool> StoreAsync(
        MqttApplicationMessage message,
        IReadOnlyDictionary<Guid, DriverTag> tags,
        IPushedSampleSink sink,
        CancellationToken cancellationToken)
    {
        var topic = message.Topic;
        var result = SamplePayload.Read(message.ConvertPayloadToString(), tags);
        Report(topic, result);

        if (result.Refusal is not null)
        {
            return true;
        }

        var stored = true;

        // Three independent things; one failing does not keep the others from being tried.
        if (result.SentAtUtc is { } sentAt)
        {
            // The sender's clock when it sent, carried forward by however long the broker held
            // the message — so a backlog delivered after this Gateway was away does not read as
            // a sender whose clock is behind.
            var waited = SamplePayload.WaitedInBroker(message.MessageExpiryInterval);
            stored &= await HandOverAsync(
                topic, "the sender's clock", () => sink.ReportSourceClockAsync(sentAt + waited, cancellationToken)).ConfigureAwait(false);
        }

        foreach (var loss in result.Lost)
        {
            stored &= await HandOverAsync(
                topic, $"loss report {loss.LossId}", () => sink.ReportLossAsync(loss, cancellationToken)).ConfigureAwait(false);
        }

        if (result.Accepted.Count > 0)
        {
            stored &= await HandOverAsync(
                topic, "samples", () => sink.AcceptAsync(result.Accepted, cancellationToken)).ConfigureAwait(false);
        }

        return stored;
    }

    private async Task<bool> HandOverAsync(string topic, string what, Func<Task> handOver)
    {
        try
        {
            await handOver().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Core reports its own failures (a historian that is down still leaves readers and
            // alarms told, ADR-0013); here it is only said where it came from.
            _logger.LogError(exception, "{What} from {Topic} could not be recorded.", what, topic);
            return false;
        }
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Closing a connection that is already going is not worth failing over.
        }
    }

    private void Report(string topic, SamplePayloadResult result)
    {
        if (result.Refusal is { } refusal)
        {
            _logger.LogWarning("Refused a message on {Topic}: {Reason}. Acknowledged: delivering it again would not change that.", topic, refusal);
            return;
        }

        foreach (var reason in result.Rejected)
        {
            _logger.LogWarning("Refused a sample on {Topic}: {Reason}.", topic, reason);
        }

        if (result.UnknownTags > 0)
        {
            _logger.LogWarning(
                "{Count} sample(s) on {Topic} named a tag this device does not have; not accepted.",
                result.UnknownTags,
                topic);
        }
    }
}
