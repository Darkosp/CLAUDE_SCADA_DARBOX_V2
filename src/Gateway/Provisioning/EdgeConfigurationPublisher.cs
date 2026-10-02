using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Derives each edge's configuration from the catalogue and publishes it, retained, to that edge's
/// own topic (ADR-0019 §4). The cloud is the source of truth; an edge never reads its devices from
/// a file an operator keeps in step by hand.
/// </summary>
/// <remarks>
/// <para>
/// Its own client, not a device's: the Gateway's only MQTT client until now belonged to a pushing
/// device and could only subscribe. This one publishes, and it must survive a device being
/// reconfigured or removed, so it is not tied to any device's lifetime.
/// </para>
/// <para>
/// Woken by the catalogue being replaced — the one thing that happens after every configuration
/// write (ADR-0019 §4) — and once at startup. Nothing is published twice: a configuration whose
/// content hash is the one already published is left alone, so an unrelated edit does not make
/// every edge restart its acquisition.
/// </para>
/// <para>
/// After every connection, not only the first, everything is published again. A broker that has
/// just come back may hold no retained messages at all, and a retained message is the only copy
/// of a configuration that outlives the process that sent it.
/// </para>
/// <para>
/// The same connection carries the other direction: each edge's declaration of the drivers it has
/// (ADR-0019 §8), handed to <see cref="EdgeDriverDeclarations"/>. One client, because the broker
/// knows this Gateway by its certificate and a second connection would be the same identity twice.
/// </para>
/// <para>
/// An edge that has been deleted is published a configuration with no devices, retained, so a
/// later edge of the same name does not inherit the one before it.
/// </para>
/// </remarks>
public sealed class EdgeConfigurationPublisher : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly TagCatalogSource _catalogSource;
    private readonly EdgeDriverDeclarations _declarations;
    private readonly EdgeProvisioningOptions _options;
    private readonly ILogger<EdgeConfigurationPublisher> _logger;
    private readonly TimeProvider _clock;

    /// <summary>What was last published, by edge id: its name — a deleted edge still needs its
    /// topic — and the revision that was sent there.</summary>
    private readonly ConcurrentDictionary<Guid, Published> _published = new();

    /// <summary>Raised whenever the catalogue is replaced, to wake the loop.</summary>
    private readonly SemaphoreSlim _changed = new(0);

    public EdgeConfigurationPublisher(
        TagCatalogSource catalogSource,
        EdgeDriverDeclarations declarations,
        IOptions<EdgeProvisioningOptions> options,
        ILogger<EdgeConfigurationPublisher> logger,
        TimeProvider? clock = null)
    {
        _catalogSource = catalogSource;
        _declarations = declarations;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        // The catalogue is replaced, never edited, so this is the whole of "configuration changed"
        // (ADR-0019 §4). Subscribed here rather than called from the reloader, so that any path
        // that installs a catalogue is served, not only the one that exists today.
        _catalogSource.Changed += (_, _) => _changed.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Edge configuration publishing is off; every edge keeps the configuration it holds.");
            return;
        }

        if (_options.Problems() is { Count: > 0 } problems)
        {
            _logger.LogError(
                "Edge configuration publishing is on but cannot connect: {Problems}. Nothing is published, and every edge keeps the configuration it holds.",
                string.Join("; ", problems));
            return;
        }

        using var client = new MqttClientFactory().CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            // Its own id: the broker keys a persistent session to it, and it must not be the id of
            // any device's client, which a device's removal would take away with it.
            .WithClientId("scada-gateway-provisioning")
            // MQTT 5, as everywhere else on this link: a refusal is a reason code, not a silent
            // acknowledgement (ADR-0017).
            .WithProtocolVersion(MqttProtocolVersion.V500);

        if (_options.UsesTls)
        {
            builder = builder.WithMutualTls(_options.TlsFiles);
        }

        var connection = builder.Build();

        // The other direction on the same client (ADR-0019 §8): every edge's declaration arrives
        // here. One client, because a second connection under the same certificate would be the
        // same identity twice, and because this one already holds the connection that carries the
        // conversation.
        client.ApplicationMessageReceivedAsync += async message =>
        {
            var application = message.ApplicationMessage;

            try
            {
                await _declarations.HandleAsync(
                    application.Topic,
                    application.ConvertPayloadToString(),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A declaration that cannot be recorded must not take the publisher down with it:
                // every edge's configuration still has to be published.
                _logger.LogError(exception, "Handling a message on {Topic} failed.", application.Topic);
            }
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

                        // Subscribed before anything is published, so an edge's declaration that is
                        // already retained on the broker is read rather than merely overwritten by
                        // this connection's own traffic.
                        await client.SubscribeAsync(
                            new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(_options.DriversTopicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build(),
                            stoppingToken).ConfigureAwait(false);

                        _published.Clear();
                        _logger.LogInformation(
                            "Connected to the broker at {Host}:{Port}; publishing each edge's configuration under {Prefix} and reading their declarations on {Filter}.",
                            _options.Host,
                            _options.Port,
                            _options.TopicPrefix,
                            _options.DriversTopicFilter);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            "Cannot reach the broker at {Host}:{Port} ({Reason}); no configuration is being published.",
                            _options.Host,
                            _options.Port,
                            exception.Message);
                        await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                try
                {
                    if (!await PublishAllAsync(client, stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Deriving a configuration reads the live catalogue, and a configuration that
                    // cannot be built is no reason to stop publishing for good: the next change
                    // tries again, and meanwhile every edge keeps the configuration it holds.
                    _logger.LogError(exception, "Deriving the edges' configurations failed; trying again.");
                    await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await _changed.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
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
    /// Publishes every edge's configuration that differs from what was last sent. False when a
    /// publish failed, so the caller waits and tries again rather than waiting for a change that
    /// may never come.
    /// </summary>
    private async Task<bool> PublishAllAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var catalog = _catalogSource.Current;
        var present = new HashSet<Guid>();

        foreach (var edge in catalog.Edges)
        {
            present.Add(edge.Id);

            var devices = EdgeConfigurationBuilder.DevicesFor(catalog, edge);
            var revision = EdgeConfigurationPayload.RevisionOf(devices);

            if (_published.TryGetValue(edge.Id, out var last) && last.Revision == revision)
            {
                continue;
            }

            var topic = _options.ConfigurationTopic(edge.Name);
            if (!await PublishAsync(client, topic, EdgeConfigurationPayload.Write(devices, _clock.GetUtcNow()), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _published[edge.Id] = new Published(edge.Name, revision);
            _logger.LogInformation(
                "Published the configuration of edge {Edge} to {Topic}: {Count} device(s), revision {Revision}.",
                edge.Name,
                topic,
                devices.Count,
                revision);
        }

        foreach (var (edgeId, last) in _published)
        {
            if (present.Contains(edgeId))
            {
                continue;
            }

            // The edge is gone. Its topic keeps its name, so the retained configuration has to be
            // emptied: an edge configured later under this name must start reading nothing, not
            // the devices of the edge that used to answer to it.
            var topic = _options.ConfigurationTopic(last.Name);
            var empty = EdgeConfigurationPayload.Write([], _clock.GetUtcNow());
            if (!await PublishAsync(client, topic, empty, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _published[edgeId] = new Published(last.Name, EdgeConfigurationPayload.RevisionOf([]));
            _logger.LogInformation("Edge {Edge} is gone; {Topic} now holds a configuration of no devices.", last.Name, topic);
        }

        return true;
    }

    /// <summary>Publishes one configuration, retained, at QoS 1; true only when the broker took it.</summary>
    private async Task<bool> PublishAsync(IMqttClient client, string topic, string payload, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    // Retained, so an edge that connects an hour from now is configured the moment
                    // it subscribes, without the cloud having to notice it arrived. No expiry: a
                    // configuration does not go stale the way a sample does (EdgeConfigurationPayload).
                    .WithRetainFlag()
                    .Build(),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return true;
            }

            _logger.LogWarning("The broker refused the configuration for {Topic}: {Reason}.", topic, result.ReasonCode);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Publishing the configuration for {Topic} failed ({Reason}).", topic, exception.Message);
            return false;
        }
    }

    private sealed record Published(string Name, string Revision);
}
