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

// ---- request bodies -------------------------------------------------------

public sealed record CreateFolderRequest(string Name, Guid? ParentFolderId);

public sealed record UpdateFolderRequest(string Name, Guid? ParentFolderId);

/// <param name="ScanIntervalMs">
/// Required for a polled device; must be absent for a pushing one, which has no scan interval (ADR-0016).
/// </param>
public sealed record SaveDeviceRequest(
    string Name,
    string DriverKey,
    IReadOnlyDictionary<string, string> ConnectionSettings,
    int? ScanIntervalMs,
    Guid? FolderId);

public sealed record SaveTagRequest(
    string Name,
    string ValueKind,
    UnitDto? Unit,
    string SourceAddress,
    bool IsWritable);

/// <summary>A driver this build has, and whether it pushes rather than being polled (ADR-0016).</summary>
public sealed record DriverDto(string Key, bool Pushing);
