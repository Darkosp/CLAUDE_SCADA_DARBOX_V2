using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Uplink;

/// <summary>
/// Sends the buffer to the broker, oldest first, and removes a batch only once the broker has
/// acknowledged it (ADR-0017). When the broker is unreachable it keeps trying; the buffer keeps
/// filling meanwhile, and nothing is lost unless it fills past its bound — which is recorded.
/// </summary>
/// <remarks>
/// At least once: a batch the broker received but whose acknowledgement was lost is sent again.
/// The cloud Gateway makes ingestion idempotent per (tag, source timestamp) rather than trusting
/// that duplicates will not happen (ADR-0017, Phase 7 step 4).
/// </remarks>
public sealed class UplinkService : BackgroundService
{
    internal const int BatchSize = 500;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly EdgeOptions _options;
    private readonly SampleBuffer _buffer;
    private readonly ILogger<UplinkService> _logger;

    public UplinkService(IOptions<EdgeOptions> options, SampleBuffer buffer, ILogger<UplinkService> logger)
    {
        _options = options.Value;
        _buffer = buffer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var connection = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Broker.Host, _options.Broker.Port)
            .WithClientId($"scada-edge-{_options.Id}")
            // MQTT 5, not 3.1.1: a 3.1.1 PUBACK has no reason code, and a broker that refuses a
            // publish — an ACL outside this edge's prefix — acknowledges it all the same. Removing
            // on that acknowledgement would delete samples the broker threw away (ADR-0017).
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!client.IsConnected)
                {
                    try
                    {
                        await client.ConnectAsync(connection, stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation(
                            "Connected to the broker at {Host}:{Port}; sending on {Topic}.",
                            _options.Broker.Host,
                            _options.Broker.Port,
                            _options.SamplesTopic);
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
                if (batch.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (await SendAsync(client, batch, stoppingToken).ConfigureAwait(false))
                {
                    // Removed only now, after the broker has said it has them.
                    _buffer.Acknowledge(batch[^1].Sequence);
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
    private async Task<bool> SendAsync(IMqttClient client, IReadOnlyList<BufferedSample> batch, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(_options.SamplesTopic)
                    .WithPayload(SamplePayload.Write(batch.Select(sample => sample.Reading)))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
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
