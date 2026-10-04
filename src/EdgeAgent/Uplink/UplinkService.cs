using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Uplink;

/// <summary>
/// The edge's connection to the cloud: it sends the buffer, oldest first, receives the
/// configuration this edge is to read, and says which drivers this build has (ADR-0017,
/// ADR-0019 §4, §8).
/// </summary>
/// <remarks>
/// <para>
/// At least once: a batch the broker received but whose acknowledgement was lost is sent again.
/// The cloud Gateway makes ingestion idempotent per (tag, source timestamp) rather than trusting
/// that duplicates will not happen (ADR-0017). A batch leaves the buffer only once the broker has
/// acknowledged it; when the broker is unreachable the service keeps trying, the buffer keeps
/// filling meanwhile, and nothing is lost unless it fills past its bound — which is recorded.
/// </para>
/// <para>
/// Every message also carries this edge's clock at the moment of sending, and any lost windows
/// not yet reported. A report is marked sent under the same acknowledgement as the samples it
/// travelled with; until then it goes out again with the next message.
/// </para>
/// <para>
/// Sending and receiving share one client deliberately. The cloud's broker takes a client's identity
/// from its certificate and forces the client id to it (ADR-0017), so a second connection from this
/// edge would displace this one and the edge would spend its life being disconnected by itself.
/// The configuration topic is subscribed to on connecting, and its message is retained, so the
/// configuration in force arrives again after every reconnect (ADR-0019 §5).
/// </para>
/// </remarks>
public sealed class UplinkService : BackgroundService
{
    internal const int BatchSize = 500;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly EdgeOptions _options;
    private readonly SampleBuffer _buffer;
    private readonly EdgeConfigurationConsumer _configuration;
    private readonly EdgeUnreadableDevices _unreadable;
    private readonly EdgeWriteExecutor _writes;
    private readonly IReadOnlyList<string> _driverKeys;
    private readonly ILogger<UplinkService> _logger;
    private readonly TimeProvider _clock;

    /// <summary>
    /// What the last declaration this edge sent said about the devices it cannot read, so the same
    /// one is not sent again on every pass (ADR-0021).
    /// </summary>
    private string _declaredUnreadable = string.Empty;

    public UplinkService(
        IOptions<EdgeOptions> options,
        SampleBuffer buffer,
        EdgeConfigurationConsumer configuration,
        EdgeUnreadableDevices unreadable,
        EdgeWriteExecutor writes,
        IEnumerable<IDeviceDriverFactory> factories,
        ILogger<UplinkService> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _buffer = buffer;
        _configuration = configuration;
        _unreadable = unreadable;
        _writes = writes;
        // The drivers this build has, asked of the same factories the acquisition service scans
        // through, so what this edge declares and what it can actually read cannot disagree.
        _driverKeys = factories.Select(factory => factory.DriverKey).ToList();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Broker.Host, _options.Broker.Port)
            .WithClientId($"scada-edge-{_options.Id}")
            // MQTT 5, not 3.1.1: a 3.1.1 PUBACK has no reason code, and a broker that refuses a
            // publish — an ACL outside this edge's prefix — acknowledges it all the same. Removing
            // on that acknowledgement would delete samples the broker threw away (ADR-0017).
            .WithProtocolVersion(MqttProtocolVersion.V500);

        if (_options.Broker.UsesTls)
        {
            builder = builder.WithMutualTls(_options.Broker.TlsFiles);
        }

        var connection = builder.Build();

        // Two things arrive on this connection, told apart by their topic: the configuration the
        // cloud derived for this edge (ADR-0019 §4), and a write it is asking for (ADR-0023). Both
        // are handled on the client's own thread and finished before the next message is read: a
        // configuration is a few kilobytes of JSON and one row in SQLite, and a write is one
        // operator's action that the cloud is waiting on.
        client.ApplicationMessageReceivedAsync += async message =>
        {
            var topic = message.ApplicationMessage.Topic;

            if (topic == _options.WritesTopic)
            {
                await HandleWriteAsync(client, message.ApplicationMessage.ConvertPayloadToString(), stoppingToken)
                    .ConfigureAwait(false);

                return;
            }

            if (topic != _options.ConfigurationTopic)
            {
                _logger.LogWarning("Ignoring a message on {Topic}, which is not this edge's topic.", topic);
                return;
            }

            _configuration.Accept(message.ApplicationMessage.ConvertPayloadToString());
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!client.IsConnected)
                {
                    try
                    {
                        await client.ConnectAsync(connection, stoppingToken).ConfigureAwait(false);

                        await client.SubscribeAsync(
                            new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(_options.ConfigurationTopic, MqttQualityOfServiceLevel.AtLeastOnce)
                                // The write topic too, and subscribing to something that is not
                                // retained is the point: a write an edge receives the moment it
                                // reconnects is one whose moment has passed (ADR-0023 §3).
                                .WithTopicFilter(_options.WritesTopic, MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build(),
                            stoppingToken).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Connected to the broker at {Host}:{Port}; sending on {SamplesTopic} and reading {ConfigurationTopic}.",
                            _options.Broker.Host,
                            _options.Broker.Port,
                            _options.SamplesTopic,
                            _options.ConfigurationTopic);

                        // After every connection, not only the first: a broker that has just come
                        // back may hold no retained messages at all, and this one is how the cloud
                        // knows which devices this edge can be sent (ADR-0019 §8). Sending samples
                        // does not depend on it, so a refusal here is reported and retried at the
                        // next connection rather than ending the loop.
                        await DeclareDriversAsync(client, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            "Cannot reach the broker at {Host}:{Port} ({Reason}); {Pending} sample(s) wait in the buffer.",
                            _options.Broker.Host,
                            _options.Broker.Port,
                            exception.Message,
                            _buffer.Account().Pending);
                        await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // The set of devices this edge cannot read is decided when a configuration is
                // accepted, and the cloud has to be told when it changes (ADR-0021). Sent only on a
                // change: an unchanged set is a declaration the cloud already holds, and the topic
                // is retained, so there is nothing to refresh.
                if (Signature() != _declaredUnreadable)
                {
                    await DeclareDriversAsync(client, stoppingToken).ConfigureAwait(false);
                }

                var batch = _buffer.Peek(BatchSize);
                var losses = _buffer.TakeLossesToSend();
                if (batch.Count == 0 && losses.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (await SendAsync(client, batch, losses, stoppingToken).ConfigureAwait(false))
                {
                    // Removed, and reported, only now — after the broker has said it has them.
                    _buffer.Acknowledge(
                        batch.Count > 0 ? batch[^1].Sequence : null,
                        losses.Select(loss => loss.LossId).ToList());
                }
                else
                {
                    await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. Whatever was not acknowledged is still in the buffer for next time.
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Leaving anyway.
                }
            }
        }
    }

    /// <summary>
    /// Says which drivers this build has, and which assigned devices it cannot read, retained, on
    /// this edge's own topic (ADR-0019 §8, ADR-0021). False when the broker did not take it; the
    /// caller carries on sending samples regardless.
    /// </summary>
    private async Task<bool> DeclareDriversAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var unreadable = _unreadable.Current;
        var signature = Signature();
        var payload = EdgeDriversPayload.Write(_driverKeys, unreadable);

        try
        {
            var result = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(_options.DriversTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    // Retained, and no expiry: the cloud may be away while this edge declares
                    // itself, and a fact about a build does not go stale the way a sample does
                    // (EdgeDriversPayload).
                    .WithRetainFlag()
                    .Build(),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Recorded only after the broker took it: a declaration that failed has not been
                // sent, so the next pass must try again rather than decide it is unchanged.
                _declaredUnreadable = signature;

                _logger.LogInformation(
                    "Declared {Count} driver(s) on {DriversTopic}: {Drivers}.",
                    _driverKeys.Count,
                    _options.DriversTopic,
                    string.Join(", ", _driverKeys));

                if (unreadable.Count > 0)
                {
                    _logger.LogWarning(
                        "This edge cannot read {Count} device(s) assigned to it, and has said so on {DriversTopic}: {Devices}.",
                        unreadable.Count,
                        _options.DriversTopic,
                        string.Join(", ", unreadable.Select(device => $"{device.Device} (needs '{device.Driver}')")));
                }

                return true;
            }

            _logger.LogWarning(
                "The broker refused this edge's declaration on {DriversTopic}: {Reason}. The cloud keeps the last one it read, and this is tried again at the next connection.",
                _options.DriversTopic,
                result.ReasonCode);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Declaring this edge's drivers on {DriversTopic} failed ({Reason}); it is tried again at the next connection.",
                _options.DriversTopic,
                exception.Message);
            return false;
        }
    }

    /// <summary>
    /// What the current set of unreadable devices would say, as one string, so a pass can tell
    /// whether the declaration the cloud holds is still the current one (ADR-0021).
    /// </summary>
    private string Signature() =>
        string.Join(
            "\n",
            _unreadable.Current.Select(device => $"{device.Device}\u0000{device.Driver}"));

    /// <summary>
    /// Carries out one write the cloud asked for and publishes the result (ADR-0023).
    /// </summary>
    /// <remarks>
    /// <b>An answer is always sent when the request could be read at all</b>, including when the
    /// write failed: a request that is understood and then left unanswered would have the cloud
    /// wait out its deadline and tell an operator "not confirmed" about a write the edge knows
    /// perfectly well did not happen. The one case with no answer is a request that could not be
    /// read — there is no id to answer with, so nothing is sent and the cloud's deadline is the
    /// only true thing to report.
    /// </remarks>
    private async Task HandleWriteAsync(IMqttClient client, string payload, CancellationToken cancellationToken)
    {
        var request = WritePayload.ReadRequest(payload);

        if (request.Refusal is not null)
        {
            _logger.LogWarning("A write from the cloud could not be read and is ignored: {Refusal}", request.Refusal);
            return;
        }

        var result = await _writes.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        var reply = WritePayload.WriteResult(request.WriteId, request.TagId, result.Written, result.Reason);

        try
        {
            // Not retained, for the reason the request is not: the cloud is waiting for this now,
            // and an answer nobody is waiting for is not worth keeping (ADR-0023 §3).
            var published = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(_options.WriteResultsTopic)
                    .WithPayload(reply)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                cancellationToken).ConfigureAwait(false);

            if (!published.IsSuccess)
            {
                _logger.LogWarning(
                    "The broker refused this edge's answer to write {Write}; the cloud will report it as not confirmed.",
                    request.WriteId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Publishing the answer to write {Write} failed; the cloud will report it as not confirmed.",
                request.WriteId);
        }
    }

    /// <summary>Publishes one batch at QoS 1; true only when the broker acknowledged it.</summary>
    private async Task<bool> SendAsync(
        IMqttClient client,
        IReadOnlyList<BufferedSample> batch,
        IReadOnlyList<LostWindow> losses,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = SamplePayload.Write(
                batch.Select(sample => sample.Reading),
                losses.Select(loss => new SourceLoss(loss.LossId, loss.Count, loss.FromSourceUtc, loss.ToSourceUtc)),
                _clock.GetUtcNow());

            var result = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(_options.SamplesTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    // Not to expire anything: the broker counts it down while it holds the
                    // message, which tells the receiver how long it waited (SamplePayload).
                    .WithMessageExpiryInterval(SamplePayload.MessageExpirySeconds)
                    .Build(),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return true;
            }

            _logger.LogWarning("The broker refused a batch of {Count}: {Reason}.", batch.Count, result.ReasonCode);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Sending a batch of {Count} failed ({Reason}); it stays in the buffer.", batch.Count, exception.Message);
            return false;
        }
    }
}
