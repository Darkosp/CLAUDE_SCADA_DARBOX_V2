namespace ScadaDarbox.Core.Model;

/// <summary>
/// A plant-side agent that acquires devices and ships their samples to the cloud over the
/// edge-to-cloud link (ADR-0017), and whose configuration the cloud derives from the devices
/// assigned to it and delivers over that same link (ADR-0019).
/// </summary>
/// <remarks>
/// Its name is its identity, not just a label: it is the name in the edge's client certificate
/// and the segment the broker carries its topics under (ADR-0017), so it is unique across the
/// tenant and cannot be display-only the way a folder or device name is.
/// </remarks>
public sealed class Edge
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Owning tenant (ADR-0004). An edge hangs off the tenant rather than a site, because its
    /// name is the identity in its certificate and must be unique across the whole deployment —
    /// which uniqueness within one site would not give.
    /// </summary>
    public required Guid TenantId { get; init; }

    /// <summary>Identity and display name in one. Unique within the tenant, ignoring case.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Gateway device that carries this edge's link — the pushing device subscribing to the
    /// topics the edge publishes under (ADR-0016, ADR-0017) — or null while the edge has no link
    /// yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link is named here rather than by each device the edge reads, because one link is
    /// either silent or not, whatever it carries. That is also where the staleness limit lives:
    /// it is a property of the transport (ADR-0016), and for a device an edge reads the transport
    /// is this link — the Gateway never opens a connection to the device itself.
    /// </para>
    /// <para>
    /// An edge with no link device may have no devices assigned to it. A device an edge reads is
    /// not polled by the Gateway, so assigning one before there is a link to carry its tags would
    /// leave them with no source at all; the repository refuses that, by name, rather than
    /// leaving the system in a state where nothing reads them and nothing says so.
    /// </para>
    /// </remarks>
    public Guid? LinkDeviceId { get; set; }

    /// <summary>
    /// The driver keys this edge has said it has, or null when it has never said (ADR-0019 §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared by the edge and not configured here: which driver keys exist is a fact about the
    /// build running at the plant, and the cloud's own list is not a substitute for it — the
    /// Gateway registers a driver the edge does not run, and an edge may one day run one the cloud
    /// does not.
    /// </para>
    /// <para>
    /// Null and empty are different facts, and the difference is why this is nullable. Null means
    /// nobody has told us — an edge that has never connected, which is the ordinary state while a
    /// plant is being configured — and empty means the edge said it has none. Reading the first as
    /// the second would refuse every assignment to an edge that is merely not switched on yet.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? DeclaredDriverKeys { get; set; }

    /// <summary>
    /// When the cloud last read a declaration from this edge, or null when it never has. The
    /// cloud's own time: an edge's clock is neither trusted nor overwritten (ADR-0017).
    /// </summary>
    public DateTimeOffset? DriversDeclaredAt { get; set; }

    /// <summary>
    /// The devices this edge has been assigned and has said it cannot read, or null when it has
    /// never said (ADR-0021).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction ADR-0019 §8 could not see. That clause refuses a device at the save that
    /// assigns it to an edge which has declared it lacks the driver — but a device assigned months
    /// ago is never re-examined, so an edge redeployed without a driver it used to have reads
    /// nothing from that device, its tags go Bad by the staleness rule (ADR-0016), and no screen or
    /// journal explains why. The edge reports it on the declaration it already makes.
    /// </para>
    /// <para>
    /// Null and empty are different facts here too, for the reason <see cref="DeclaredDriverKeys"/>
    /// gives. Null means no declaration has said anything about it — an edge that has never
    /// connected, or one running a build whose message predates this field — and empty means the
    /// edge has said it can read everything assigned to it. Neither is a fault: it is a report, and
    /// the assignment is deliberately left alone, because an edge must not be able to rewrite a
    /// plant's configuration by failing to read it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<EdgeUnreadableDevice>? UnreadableDevices { get; set; }
}

/// <summary>
/// A device an edge has been assigned and cannot open because its build has no driver for it
/// (ADR-0021). Named rather than identified: the operator reading the log, the audit row or the
/// screen is reading names, and the cloud resolves the name to the device it assigned.
/// </summary>
/// <param name="Device">The device's name, as the cloud gave it to the edge.</param>
/// <param name="Driver">The driver key the device needs and the edge's build does not have.</param>
public sealed record EdgeUnreadableDevice(string Device, string Driver);
