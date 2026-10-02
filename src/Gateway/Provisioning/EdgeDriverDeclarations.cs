using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Security;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Reads what an edge says about itself — the driver keys that build has (ADR-0019 §8) — from the
/// topic the edge publishes it on, and records it so the cloud can refuse a device that edge cannot
/// read.
/// </summary>
/// <remarks>
/// <para>
/// The other direction of the conversation <see cref="EdgeConfigurationPublisher"/> holds. A
/// declaration is the one fact the cloud cannot derive: which driver keys exist is a property of
/// the build at the plant, and the Gateway's own list is a different list — it runs a driver no
/// edge reads with, and an edge may one day run one the cloud does not.
/// </para>
/// <para>
/// Handled on the MQTT client's thread and completed before the next message is read, as the edge
/// treats an arriving configuration: a declaration is a few hundred bytes and two statements, and a
/// queue between them would only be somewhere for it to wait while an operator is refused an
/// assignment over a driver the edge has.
/// </para>
/// <para>
/// A name with no edge behind it is reported and dropped, and nothing is invented for it. The
/// broker confines a publisher to the name in its certificate (ADR-0017), so a name the catalogue
/// does not know is an edge that has been deleted, or a certificate older than the catalogue.
/// </para>
/// </remarks>
public sealed class EdgeDriverDeclarations
{
    private const string TopicSuffix = "/drivers";

    private readonly TagCatalogSource _catalogSource;
    private readonly IEdgeRepository _edges;
    private readonly ConfigurationReloader _reloader;
    private readonly IAuditLog _audit;
    private readonly EdgeProvisioningOptions _options;
    private readonly ILogger<EdgeDriverDeclarations> _logger;
    private readonly TimeProvider _clock;

    public EdgeDriverDeclarations(
        TagCatalogSource catalogSource,
        IEdgeRepository edges,
        ConfigurationReloader reloader,
        IAuditLog audit,
        IOptions<EdgeProvisioningOptions> options,
        ILogger<EdgeDriverDeclarations> logger,
        TimeProvider? clock = null)
    {
        _catalogSource = catalogSource;
        _edges = edges;
        _reloader = reloader;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Records one declaration, or reports why it was not recorded.</summary>
    public async Task HandleAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (EdgeNameOf(topic) is not { } name)
        {
            _logger.LogWarning("Ignoring a message on {Topic}, which is not an edge's declaration topic.", topic);
            return;
        }

        var declared = EdgeDriversPayload.Read(payload);
        if (declared.Refusal is not null)
        {
            _logger.LogError(
                "The declaration from edge {Edge} on {Topic} was refused: {Reasons}. The cloud keeps the last one it read.",
                name,
                topic,
                string.Join("; ", declared.Problems));
            return;
        }

        var edge = _catalogSource.Current.Edges
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (edge is null)
        {
            _logger.LogWarning(
                "Edge {Edge} declared its drivers on {Topic}, but there is no such edge. Nothing is recorded; a declaration from an edge that has been deleted changes nothing.",
                name,
                topic);
            return;
        }

        // The cloud's own clock: what the edge sent carries no time and would not be trusted with
        // one (ADR-0017).
        var declaredAt = _clock.GetUtcNow();

        if (!await _edges.RecordDriversAsync(edge.Id, declared.Drivers, declaredAt, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Edge {Edge} declared its drivers, but it is no longer in the catalogue. Nothing is recorded.",
                name);
            return;
        }

        await _reloader.ReloadAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Edge {Edge} declared {Count} driver(s): {Drivers}.",
            name,
            declared.Drivers.Count,
            declared.Drivers.Count == 0 ? "(none)" : string.Join(", ", declared.Drivers));

        // What the declaration says about the assignment that already exists. A device assigned to
        // this edge whose driver the edge has just said it does not have is a state in which
        // nothing reads that device's tags, and until now only the edge knew. It is recorded and
        // named, and the assignment is left alone: what an edge reads must not change because the
        // edge answered (ADR-0019 §8).
        var unreadable = _catalogSource.Current.Devices
            .Where(device => device.EdgeId == edge.Id && !declared.Drivers.Contains(device.DriverKey, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var device in unreadable)
        {
            _logger.LogWarning(
                "Device {Device} is assigned to edge {Edge}, which has declared it has no '{Driver}' driver. The device will not be read, and the assignment is left as it is.",
                device.Name,
                name,
                device.DriverKey);
        }

        await _audit.AppendAsync(
            new AuditEntry(
                // No user: this is a fact an edge reported about itself, and it has a certificate
                // rather than an account (ADR-0019 §8).
                ActorUserId: null,
                "edge.drivers_declared",
                "edge",
                edge.Id,
                Audit.Detail(
                    ("edge", name),
                    ("drivers", declared.Drivers),
                    ("declaredAtUtc", declaredAt),
                    ("unreadableDeviceIds", unreadable.Select(device => device.Id).ToList()))),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The edge name in <c>{prefix}/{name}/drivers</c>, or null when the topic is another one.</summary>
    private string? EdgeNameOf(string topic)
    {
        var prefix = _options.TopicPrefix.TrimEnd('/') + "/";

        if (!topic.StartsWith(prefix, StringComparison.Ordinal)
            || !topic.EndsWith(TopicSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = topic[prefix.Length..^TopicSuffix.Length];

        // One segment exactly: a declaration cannot come from a topic that names two edges, and the
        // broker's wildcard is not a name.
        return name.Length == 0 || name.Contains('/') ? null : name;
    }
}
