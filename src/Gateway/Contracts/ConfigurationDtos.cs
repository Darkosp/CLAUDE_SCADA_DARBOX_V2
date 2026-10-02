using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>
/// A unit on the wire: the dimension and its conversion to SI, not a bare symbol
/// (ADR-0005). The symbol is for display and is derived from the rest.
/// </summary>
public sealed record UnitDto(string Symbol, string Dimension, double FactorToSi, double OffsetToSi)
{
    public static UnitDto? From(UnitOfMeasure? unit) => unit is null
        ? null
        : new UnitDto(unit.Symbol, unit.Dimension.ToString(), unit.FactorToSi, unit.OffsetToSi);

    /// <summary>Rebuilds the domain unit, or null when no unit was supplied.</summary>
    /// <exception cref="ArgumentException">The dimension is not one this build knows.</exception>
    public UnitOfMeasure ToDomain() =>
        Enum.TryParse<Dimension>(Dimension, ignoreCase: true, out var dimension)
            ? new UnitOfMeasure(Symbol, dimension, FactorToSi, OffsetToSi)
            : throw new ArgumentException($"Unknown dimension '{Dimension}'.", nameof(Dimension));
}

/// <summary>A tag as it appears in the browse tree.</summary>
public sealed record TreeTagDto(
    Guid Id,
    string Name,
    string ValueKind,
    UnitDto? Unit,
    string SourceAddress,
    bool IsWritable);

/// <summary>A device and the tags it owns.</summary>
public sealed record TreeDeviceDto(
    Guid Id,
    string Name,
    string DriverKey,
    IReadOnlyDictionary<string, string> ConnectionSettings,
    int? ScanIntervalMs,
    Guid? FolderId,
    IReadOnlyList<TreeTagDto> Tags);

/// <summary>A folder, with whatever sits inside it.</summary>
public sealed record TreeFolderDto(
    Guid Id,
    string Name,
    Guid? ParentFolderId,
    IReadOnlyList<TreeFolderDto> Folders,
    IReadOnlyList<TreeDeviceDto> Devices);

/// <summary>
/// One site's whole browse tree. Folders and devices at the top level are those sitting
/// directly under the site.
/// </summary>
public sealed record SiteTreeDto(
    Guid SiteId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<TreeFolderDto> Folders,
    IReadOnlyList<TreeDeviceDto> Devices);

/// <summary>A site in the site list, without its contents.</summary>
public sealed record SiteDto(Guid Id, string Name, string TimeZoneId);

/// <summary>
/// An edge as the API shows it: the name that is its identity, the device carrying its link, and
/// the devices it reads (ADR-0019).
/// </summary>
/// <param name="LinkDeviceId">
/// The Gateway device that carries this edge's link, or null while it has none. An edge with no
/// link may have no devices assigned to it.
/// </param>
/// <param name="DeviceIds">
/// The devices this edge reads, in no particular order. Which devices an edge reads is not set
/// here: it is an ordinary edit of the device, and <c>EdgeId</c> on its save request is the only
/// way it changes (ADR-0019 §2).
/// </param>
/// <param name="DeclaredDriverKeys">
/// The driver keys this edge says its own build has, or <b>null</b> when it has never said
/// (ADR-0019 §8). Null and empty are different answers and are shown differently: null is "nobody
/// has told us", empty is "this edge says it has none". Neither is ever filled in from the
/// Gateway's own drivers, which are a different list.
/// </param>
/// <param name="DriversDeclaredAtUtc">
/// When the cloud read that declaration, in the cloud's own time, or null when it never has.
/// </param>
/// <param name="UnreadableDevices">
/// The devices assigned to this edge that are not being read, each with the driver it needs
/// (ADR-0021). Two facts are gathered here because an operator has one question — "is this device
/// being read?" — and it has two causes: a device assigned to an edge that declared it lacks the
/// driver (ADR-0019 §8), and a device the edge itself reported it cannot open (ADR-0021), which is
/// the case no save re-examines. Empty means every assigned device is being read. The assignment is
/// never changed by either: an edge must not be able to rewrite a plant's configuration by failing
/// to read it.
/// </param>
/// <param name="LinkStalenessSeconds">
/// How long this edge's link may be silent before the tags of the devices it reads go Bad
/// (ADR-0016, ADR-0022). The edge's own setting rather than the link device's, because the edge is
/// the link, and the device the Gateway derives is not overridable.
/// </param>
/// <param name="LinkSessionExpiryHours">
/// How long the broker queues for this edge while the Gateway is away (ADR-0022). Past it the queue
/// is discarded; the edge's own disk buffer is what makes that survivable (ADR-0017).
/// </param>
public sealed record EdgeDto(
    Guid Id,
    string Name,
    Guid? LinkDeviceId,
    IReadOnlyList<Guid> DeviceIds,
    IReadOnlyList<string>? DeclaredDriverKeys = null,
    DateTimeOffset? DriversDeclaredAtUtc = null,
    IReadOnlyList<UnreadableDeviceDto>? UnreadableDevices = null,
    int LinkStalenessSeconds = 0,
    int LinkSessionExpiryHours = 0);

/// <summary>A device an edge is assigned and is not reading, and the driver it needs (ADR-0021).</summary>
/// <param name="DeviceId">
/// The cloud's own device, resolved from the name the edge reported. Null when the name no longer
/// resolves — the report is still shown, because it is what the edge said.
/// </param>
/// <param name="Device">The device's name, as the edge reported it.</param>
/// <param name="Driver">The driver key the device needs and the edge says it does not have.</param>
/// <param name="ReportedByEdge">
/// True when the edge itself named this device (ADR-0021), false when the cloud worked it out from
/// the assignment and the declared driver keys (ADR-0019 §8). Both mean the device is not read.
/// </param>
public sealed record UnreadableDeviceDto(
    Guid? DeviceId,
    string Device,
    string Driver,
    bool ReportedByEdge);

// ---- request bodies -------------------------------------------------------

public sealed record CreateFolderRequest(string Name, Guid? ParentFolderId);

public sealed record UpdateFolderRequest(string Name, Guid? ParentFolderId);

/// <param name="ScanIntervalMs">
/// Required for a polled device; must be absent for a pushing one, which has no scan interval (ADR-0016).
/// </param>
/// <param name="EdgeId">
/// The edge that acquires this device, or null for the Gateway to poll it itself (ADR-0019).
/// Assigning and releasing are ordinary edits of the device, and this is the only way either
/// happens.
/// </param>
public sealed record SaveDeviceRequest(
    string Name,
    string DriverKey,
    IReadOnlyDictionary<string, string> ConnectionSettings,
    int? ScanIntervalMs,
    Guid? FolderId,
    Guid? EdgeId = null);

/// <param name="LinkDeviceId">
/// The device that carries this edge's link — the pushing device that subscribes to the topics the
/// edge publishes under (ADR-0016, ADR-0017). Null while the edge has none, which is allowed
/// until a device is assigned to it.
/// </param>
/// <param name="LinkStalenessSeconds">
/// How long this edge's link may be silent before the tags of the devices it reads go Bad
/// (ADR-0022). Null leaves the current value, which for a new edge is 60 seconds — the MQTT
/// driver's own default, so an edge that never mentions this behaves as one always has.
/// </param>
/// <param name="LinkSessionExpiryHours">
/// How long the broker queues for this edge while the Gateway is away (ADR-0022). Null leaves the
/// current value, which for a new edge is 720 hours.
/// </param>
public sealed record SaveEdgeRequest(
    string Name,
    Guid? LinkDeviceId,
    int? LinkStalenessSeconds = null,
    int? LinkSessionExpiryHours = null);

public sealed record SaveTagRequest(
    string Name,
    string ValueKind,
    UnitDto? Unit,
    string SourceAddress,
    bool IsWritable);

/// <summary>A driver this build has, and whether it pushes rather than being polled (ADR-0016).</summary>
public sealed record DriverDto(string Key, bool Pushing);
