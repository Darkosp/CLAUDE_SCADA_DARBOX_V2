using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Writes each edge's link device, so that nothing about an edge is typed by hand (ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// The link device is the Gateway's end of the conversation an edge starts: a real MQTT device whose
/// subscription receives what the edge publishes, and whose <c>stalenessSeconds</c> is the declared
/// limit for everything the edge carries (ADR-0016, ADR-0019 §5). Until this existed an operator
/// created it by hand, and everything they typed was either a constant of the deployment — the
/// broker, the port, the TLS flag, three certificate paths — or the edge's own name in a topic.
/// </para>
/// <para>
/// <b>One link device per edge, and deliberately not one shared device.</b> A single subscription
/// carrying every edge would give one staleness limit to all of them, so one silent plant would mark
/// every other plant's tags Bad. That is the failure ADR-0019 §5 chose one link per edge to avoid,
/// and it is why deriving the device does not collapse them (ADR-0022 §2).
/// </para>
/// <para>
/// <b>Reconciliation, not creation.</b> This runs until every edge has a link and then keeps
/// watching, because an edge can be added to a running Gateway and there is no hook that would let
/// the edge endpoint ask for a device without the endpoint knowing about drivers, settings and the
/// catalogue. It is a pass over the edges, and it writes only where something is missing — so an
/// existing link device, and with it the broker's session keyed to its id, is left exactly as it is
/// (ADR-0022 §4).
/// </para>
/// </remarks>
public sealed class LinkDeviceProvisioner : BackgroundService
{
    /// <summary>
    /// How often to look for an edge that has no link. Slow enough to be nothing on a running
    /// deployment, fast enough that an operator who has just made an edge does not notice: the
    /// alternative is a hook in every path that can create an edge.
    /// </summary>
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(2);

    private readonly TagCatalogSource _catalog;
    private readonly IDeviceRepository _devices;
    private readonly IEdgeRepository _edges;
    private readonly ConfigurationReloader _reloader;
    private readonly EdgeProvisioningOptions _options;
    private readonly ILogger<LinkDeviceProvisioner> _logger;

    /// <summary>
    /// Serialises the passes. The background loop and a reload-triggered pass can otherwise both
    /// see the same edge without a link and write two devices for it.
    /// </summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    public LinkDeviceProvisioner(
        TagCatalogSource catalog,
        IDeviceRepository devices,
        IEdgeRepository edges,
        ConfigurationReloader reloader,
        IOptions<EdgeProvisioningOptions> options,
        ILogger<LinkDeviceProvisioner> logger)
    {
        _catalog = catalog;
        _devices = devices;
        _edges = edges;
        _reloader = reloader;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Pass);

        try
        {
            do
            {
                await ProvisionAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Gives one edge its link device, if it has none, and reloads the catalogue so the edge the
    /// caller is about to answer with already names it.
    /// </summary>
    /// <remarks>
    /// Called by the save that creates an edge, so there is no window in which an edge exists
    /// without the link its samples arrive through — an assignment made in that window would be
    /// refused for a link that is about to appear. The background pass is what covers an edge that
    /// arrived some other way, and is the same code.
    /// </remarks>
    /// <returns>True when a device was written.</returns>
    public async Task<bool> ProvisionAsync(Guid edgeId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Read again rather than trusting a caller's snapshot: the edge was just written, and
            // this is the read that proves it is there and still has no link.
            var edge = _catalog.Current.Edges.FirstOrDefault(candidate => candidate.Id == edgeId);

            if (edge is null || edge.LinkDeviceId is not null)
            {
                return false;
            }

            var wrote = await WriteLinkAsync(edge, cancellationToken).ConfigureAwait(false);

            if (wrote)
            {
                await _reloader.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            return wrote;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Writes a link device for every edge that has none, and reloads the catalogue once if it
    /// wrote anything, so the assignment path sees the link in the same second it was made.
    /// </summary>
    /// <remarks>
    /// Public so it can be called on its own — a test watches the derivation without waiting for a
    /// timer, and the background loop is the same method on a schedule. The guard is here rather
    /// than in the loop for that reason: a caller reaching this method directly must meet the same
    /// refusal the loop does, or the two entry points would disagree about what the setting means.
    /// </remarks>
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        // A deployment with provisioning off has no broker to subscribe to and no certificate to
        // present, so a link device would be a device that cannot connect. Nothing is written.
        if (!_options.Enabled)
        {
            return;
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var wrote = 0;

            foreach (var edge in _catalog.Current.Edges)
            {
                if (edge.LinkDeviceId is not null)
                {
                    continue;
                }

                if (await WriteLinkAsync(edge, cancellationToken).ConfigureAwait(false))
                {
                    wrote++;
                }
            }

            if (wrote > 0)
            {
                _logger.LogInformation(
                    "Derived the link device for {Count} edge(s) (ADR-0022).",
                    wrote);

                // So the edge the operator is looking at has its link before they try to assign a
                // device to it, which is the refusal this removes: "'{Edge}' has no link device yet".
                await _reloader.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// The device, then the edge's link pointing at it. In that order: the link names a device, and
    /// the edge's own guard only lets a link be set while no device is assigned to it — which is the
    /// state an edge with no link is necessarily in.
    /// </summary>
    private async Task<bool> WriteLinkAsync(Edge edge, CancellationToken cancellationToken)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = EdgeSite(edge),
            Name = LinkDeviceName(edge),
            DriverKey = MqttDriverKey,
            ConnectionSettings = LinkSettings(edge),
            // ScanInterval is deliberately not set. A pushing device has none (ADR-0016): the
            // stored column keeps its default, nothing reads it, and the API never shows it — the
            // same thing ConfigurationEndpoints does for a device whose driver pushes.
            // The link is the Gateway's own device, not something an edge reads, so it is never
            // itself assigned to an edge. An edge that read its own link would be a loop.
            EdgeId = null,
        };

        await _devices.AddAsync(device, cancellationToken).ConfigureAwait(false);

        var linked = new Edge
        {
            Id = edge.Id,
            TenantId = edge.TenantId,
            Name = edge.Name,
            LinkDeviceId = device.Id,
            LinkStaleness = edge.LinkStaleness,
            LinkSessionExpiry = edge.LinkSessionExpiry,
        };

        try
        {
            await _edges.UpdateAsync(linked, cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigurationConflictException)
        {
            // The edge acquired a device between the read and the write, so its link is now guarded
            // and this attempt is refused. The device written above has no edge pointing at it; the
            // next pass leaves the edge alone because it has a link by then — or, if it has none
            // because it was deleted, the device is an orphan the operator can see and remove,
            // which is better than a link silently repointed under a live assignment.
            _logger.LogWarning(
                "Cannot give edge {Edge} its link device: a device was assigned to it in the meantime. It is left as it is (ADR-0022).",
                edge.Name);
            return false;
        }

        _logger.LogInformation(
            "Derived edge {Edge}'s link device {Device}: subscribing to {Topic}, silence allowed for {Staleness} s (ADR-0022).",
            edge.Name,
            device.Name,
            LinkSettings(edge)["topic"],
            (int)edge.LinkStaleness.TotalSeconds);

        return true;
    }

    /// <summary>
    /// What the MQTT driver needs to subscribe as the Gateway. Everything but the topic and the two
    /// limits is a constant of the deployment (ADR-0022 §7), read from the same options the
    /// provisioning publisher connects with — so the Gateway's subscription and its publishes agree
    /// by construction rather than by being typed the same way twice.
    /// </summary>
    private Dictionary<string, string> LinkSettings(Edge edge)
    {
        var settings = new Dictionary<string, string>
        {
            ["host"] = _options.Host,
            ["port"] = _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["topic"] = SamplesTopic(edge.Name),
            ["stalenessSeconds"] = ((int)edge.LinkStaleness.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sessionExpiryHours"] = ((int)edge.LinkSessionExpiry.TotalHours)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (_options.UsesTls)
        {
            settings["tls"] = "true";
            settings["caFile"] = _options.CaFile;
            settings["certFile"] = _options.CertFile;
            settings["keyFile"] = _options.KeyFile;
        }

        return settings;
    }

    /// <summary>
    /// Where this edge publishes its samples. The edge's own name is the whole address, and it is
    /// the name in its certificate — which is why nothing here can be typed wrong (ADR-0022).
    /// </summary>
    private string SamplesTopic(string edgeName) => $"{_options.TopicPrefix.TrimEnd('/')}/{edgeName}/samples";

    /// <summary>What an edge's link device is called: named after the edge, and recognisable.</summary>
    internal static string LinkDeviceName(Edge edge) => $"Edge {edge.Name}";

    /// <summary>
    /// The Site a link device belongs to. A device is Site-scoped (ADR-0004) and an edge is not —
    /// it hangs off the tenant, because its name is unique across the deployment — so the link takes
    /// the Site of the first device assigned to that edge and the tenant's first Site when there is
    /// none to take it from. It is a placement, not a claim: the link is the Gateway's own
    /// subscription and reads no plant.
    /// </summary>
    private Guid EdgeSite(Edge edge)
    {
        var assigned = _catalog.Current.Devices.FirstOrDefault(device => device.EdgeId == edge.Id);

        return assigned?.SiteId
            ?? _catalog.Current.Sites.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException(
                $"Cannot derive a link device for edge '{edge.Name}': the deployment has no Site to place it in (ADR-0004).");
    }

    private const string MqttDriverKey = "mqtt";
}
