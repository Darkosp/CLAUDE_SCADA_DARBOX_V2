using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Drivers;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// A pushing driver over MQTT (ADR-0016, ADR-0017): subscribes to one topic filter and hands
/// every sample it receives to Core, carrying the source's own timestamp and quality.
/// </summary>
/// <remarks>
/// It never answers "what is the value now". While the broker is unreachable it hands nothing
/// over and keeps trying to reconnect; Core's staleness rule is what turns that silence into Bad.
/// </remarks>
public sealed class MqttPushingDriver : IPushingDeviceDriver
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionCheck = TimeSpan.FromMilliseconds(500);

    private readonly MqttConnection _connection;
    private readonly ILogger _logger;
    private readonly IMqttClient _client;

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
            var result = SamplePayload.Read(message.ApplicationMessage.ConvertPayloadToString(), byId);
            Report(message.ApplicationMessage.Topic, result);

            if (result.Accepted.Count == 0)
            {
                return;
            }

            try
            {
                await sink.AcceptAsync(result.Accepted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Core reports its own failures (a historian that is down still leaves readers and
                // alarms told, ADR-0013); here it is only said where the samples came from.
                _logger.LogError(exception, "Samples from {Topic} could not all be recorded.", message.ApplicationMessage.Topic);
            }
        }

        // Added for this run and removed when it ends: the scanner runs a driver again after a
        // failure, and a handler left from the last run would hand every message over twice.
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_connection.Host, _connection.Port)
            .WithClientId(_connection.ClientId)
            .Build();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_client.IsConnected)
                {
                    try
                    {
                        await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                        await _client.SubscribeAsync(
                            new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(_connection.Topic, MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build(),
                            cancellationToken).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Subscribed to {Topic} on {Host}:{Port}.", _connection.Topic, _connection.Host, _connection.Port);
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
            // Stopped.
        }
        finally
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
        }
    }

    public async ValueTask DisposeAsync()
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
            // Closing a connection that is already going is not worth failing a shutdown over.
        }

        _client.Dispose();
    }

    private void Report(string topic, SamplePayloadResult result)
    {
        if (result.Refusal is { } refusal)
        {
            _logger.LogWarning("Refused a message on {Topic}: {Reason}.", topic, refusal);
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
