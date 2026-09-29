using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Uplink;

/// <summary>
/// The edge's connection to the cloud: it sends the buffer, oldest first, and receives the
/// configuration this edge is to read (ADR-0017, ADR-0019 §4).
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
    private readonly ILogger<UplinkService> _logger;
    private readonly TimeProvider _clock;

    public UplinkService(
        IOptions<EdgeOptions> options,
        SampleBuffer buffer,
        EdgeConfigurationConsumer configuration,
        ILogger<UplinkService> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _buffer = buffer;
        _configuration = configuration;
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

        // This edge's configuration, as the cloud publishes it (ADR-0019 §4). Handled on the
        // client's own thread and applied before the next message is read: it is a few kilobytes of
        // JSON and one row in SQLite, and a queue between the two would only be somewhere for an
        // accepted configuration to wait.
        client.ApplicationMessageReceivedAsync += message =>
        {
            var topic = message.ApplicationMessage.Topic;

            if (topic != _options.ConfigurationTopic)
            {
                _logger.LogWarning("Ignoring a message on {Topic}, which is not this edge's topic.", topic);
                return Task.CompletedTask;
            }

            _configuration.Accept(message.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
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
                                .Build(),
                            stoppingToken).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Connected to the broker at {Host}:{Port}; sending on {SamplesTopic} and reading {ConfigurationTopic}.",
                            _options.Broker.Host,
                            _options.Broker.Port,
                            _options.SamplesTopic,
                            _options.ConfigurationTopic);
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
