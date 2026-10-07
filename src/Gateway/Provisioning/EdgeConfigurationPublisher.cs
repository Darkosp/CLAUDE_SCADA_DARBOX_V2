using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ScadaDarbox.Core.Model;
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

    /// <summary>The devices left out of each edge's configuration for having no tags, by edge id,
    /// as last reported (ADR-0020). Kept so the line is written when the set changes rather than
    /// on every publish.</summary>
    private readonly ConcurrentDictionary<Guid, string> _omittedReported = new();

    /// <summary>Raised whenever the catalogue is replaced, to wake the loop.</summary>
    private readonly SemaphoreSlim _changed = new(0);

    /// <summary>The cloud's half of the write conversation (ADR-0023): it matches a result to the
    /// call waiting for it, and it is given the way to publish here because this is the type that
    /// owns the one MQTT connection the Gateway holds.</summary>
    private readonly EdgeWriteRouter _writes;

    public EdgeConfigurationPublisher(
        TagCatalogSource catalogSource,
        EdgeDriverDeclarations declarations,
        EdgeWriteRouter writes,
        IOptions<EdgeProvisioningOptions> options,
        ILogger<EdgeConfigurationPublisher> logger,
        TimeProvider? clock = null)
    {
        _catalogSource = catalogSource;
        _declarations = declarations;
        _writes = writes;
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
                // Two conversations arrive on this connection, told apart by their topic: a
                // declaration (ADR-0019 §8) and the result of a write the cloud asked for
                // (ADR-0023). The router ignores anything whose id it is not waiting for, so an
                // edge's result on the declaration topic cannot complete a write.
                if (application.Topic.EndsWith("/write-results", StringComparison.Ordinal))
                {
                    _writes.HandleResult(application.ConvertPayloadToString());
                    return;
                }

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
                                .WithTopicFilter(_options.WriteResultsTopicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build(),
                            stoppingToken).ConfigureAwait(false);

                        // Given the live client through a closure, and again after every reconnect: a
                        // sender that captured a dead one would fail silently for the life of the
                        // process, and ADR-0023's deadline would turn that into "the edge did not
                        // answer" — a wrong reason rather than a missing one.
                        _writes.UseSender((topic, payload, token) =>
                            PublishRawAsync(client, topic, payload, token));

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

            var devices = EdgeConfigurationBuilder.DevicesFor(catalog, edge, out var omitted);
            var revision = EdgeConfigurationPayload.RevisionOf(devices);

            // **Before the revision check, and that is the whole point of where it sits.** Assigning a
            // device with no tags leaves the derived configuration IDENTICAL — the device is omitted
            // whether or not it is there — so the revision does not change and nothing is published.
            // While this call sat after the `continue` below, that case logged nothing at all: an
            // operator assigned a device that would never be read and the Gateway said so only if
            // some unrelated change happened to republish the configuration. Measured on 2026-10-07:
            // the line appeared four minutes late, when a different device gained a tag.
            //
            // `ReportOmitted` already de-duplicates, which is what makes it safe to call on every
            // pass — and that de-duplication is also the evidence the placement was the mistake
            // rather than the intent, because nothing after a publish needs it.
            ReportOmitted(edge, omitted);

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
            _omittedReported.TryRemove(edgeId, out _);
            _logger.LogInformation("Edge {Edge} is gone; {Topic} now holds a configuration of no devices.", last.Name, topic);
        }

        return true;
    }

    /// <summary>
    /// Says which of an edge's devices were left out of its configuration for having no tags, once
    /// per change rather than once per publish (ADR-0020).
    /// </summary>
    /// <remarks>
    /// Not an error and not a refusal: a device assigned before its tags exist is a state an
    /// operator is passing through, and the first tag's save republishes the configuration with
    /// the device in it. It is said out loud because a device that is assigned and silently
    /// unread is the shape of finding ADR-0019 §8 closed on the other axis, and because an
    /// operator who sees nothing happen after an assignment has been told nothing.
    /// </remarks>
    private void ReportOmitted(Edge edge, IReadOnlyList<string> omitted)
    {
        if (omitted.Count == 0)
        {
            _omittedReported.TryRemove(edge.Id, out _);
            return;
        }

        var report = string.Join(", ", omitted);

        if (_omittedReported.TryGetValue(edge.Id, out var already) && already == report)
        {
            return;
        }

        _omittedReported[edge.Id] = report;
        _logger.LogInformation(
            "Edge {Edge} has {Count} device(s) assigned with no tags, so they are not in its configuration: {Devices}. The edge reads nothing from them until one of their tags is added (ADR-0020).",
            edge.Name,
            omitted.Count,
            report);
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

    /// <summary>
    /// Publishes one write request, **not retained** (ADR-0023 §3). Throws when the broker refuses
    /// it, because a write that did not leave is one the router must report as unconfirmed rather
    /// than wait out its deadline for.
    /// </summary>
    /// <remarks>
    /// The opposite of <see cref="PublishAsync"/> in the one way that matters: no retain flag. A
    /// retained write is one an edge receives the moment it reconnects, having missed the moment —
    /// a late sample is still true of its own moment, and a late command is a request to change a
    /// plant after the reason for it has passed.
    /// </remarks>
    private async Task PublishRawAsync(IMqttClient client, string topic, string payload, CancellationToken cancellationToken)
    {
        var result = await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"the broker refused it: {result.ReasonCode}");
        }
    }

    private sealed record Published(string Name, string Revision);
}
