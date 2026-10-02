using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Flat row shapes matching the configuration tables, one per table.</summary>
/// <remarks>
/// Dapper maps columns onto these by name. The projection from a row to a domain
/// object stays hand-written (ADR-0008 is explicit that Dapper does not remove it):
/// a <see cref="UnitOfMeasure"/> is assembled from four columns and must be absent
/// rather than half-built when the dimension or factor is missing.
/// </remarks>
internal static class ConfigurationRows
{
    internal sealed record SiteRow(Guid Id, Guid TenantId, string Name, string TimeZoneId)
    {
        internal Site ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            TimeZoneId = TimeZoneId,
        };
    }

    internal sealed record FolderRow(Guid Id, Guid SiteId, Guid? ParentFolderId, string Name)
    {
        internal Folder ToDomain() => new()
        {
            Id = Id,
            SiteId = SiteId,
            ParentFolderId = ParentFolderId,
            Name = Name,
        };
    }

    internal sealed record DeviceRow(
        Guid Id,
        Guid SiteId,
        Guid? FolderId,
        string Name,
        string DriverKey,
        string ConnectionSettings,
        int ScanIntervalMs,
        Guid? TemplateId,
        Guid? EdgeId)
    {
        internal Device ToDomain() => new()
        {
            Id = Id,
            SiteId = SiteId,
            FolderId = FolderId,
            Name = Name,
            DriverKey = DriverKey,
            ConnectionSettings = ConnectionSettingsJson.Deserialize(ConnectionSettings),
            ScanInterval = TimeSpan.FromMilliseconds(ScanIntervalMs),
            TemplateId = TemplateId,
            EdgeId = EdgeId,
        };
    }

    /// <summary>
    /// An edge, with the driver keys it declared. Two of the three shapes here are the reader's
    /// rather than the domain's, and both are narrowed in the projection: the declared list arrives
    /// as <see cref="Array"/> (the column is <c>text[]</c>, and an array is reported as the array
    /// type, which Dapper matches against no <c>string[]</c> parameter), and a <c>timestamptz</c>
    /// arrives as a UTC <see cref="DateTime"/>, which is not the <see cref="DateTimeOffset"/> the
    /// domain keeps.
    /// </summary>
    internal sealed record EdgeRow(
        Guid Id,
        Guid TenantId,
        string Name,
        Guid? LinkDeviceId,
        Array? DriverKeys,
        DateTime? DriversDeclaredAt,
        string? UnreadableDevices,
        int LinkStalenessSeconds,
        int LinkSessionExpiryHours)
    {
        internal Edge ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            LinkDeviceId = LinkDeviceId,
            // Null is "never declared" and empty is "declared none"; the cast keeps that difference
            // rather than flattening both into nothing.
            DeclaredDriverKeys = DriverKeys as string[],
            DriversDeclaredAt = DriversDeclaredAt is { } declaredAt
                ? new DateTimeOffset(DateTime.SpecifyKind(declaredAt, DateTimeKind.Utc))
                : null,
            // And again for ADR-0021: null is "nothing was said about what this edge cannot read"
            // and empty is "it said it can read everything assigned to it".
            UnreadableDevices = UnreadableDevicesJson.Deserialize(UnreadableDevices),
            // Not nullable, and no state they could be missing from (ADR-0022): every edge has a
            // link, and a link always has a limit.
            LinkStaleness = TimeSpan.FromSeconds(LinkStalenessSeconds),
            LinkSessionExpiry = TimeSpan.FromHours(LinkSessionExpiryHours),
        };
    }

    internal sealed record DeviceTemplateRow(Guid Id, Guid TenantId, string Name)
    {
        internal DeviceTemplate ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
        };
    }

    internal sealed record DeviceTemplateTagRow(
        Guid Id,
        Guid TemplateId,
        string Name,
        short ValueKind,
        string? UnitSymbol,
        short? UnitDimension,
        double? UnitFactorToSi,
        double? UnitOffsetToSi,
        string AddressTemplate,
        bool IsWritable)
    {
        internal DeviceTemplateTag ToDomain() => new()
        {
            Id = Id,
            TemplateId = TemplateId,
            Name = Name,
            ValueKind = (TagValueKind)ValueKind,
            Unit = UnitDimension is null || UnitFactorToSi is null
                ? null
                : new UnitOfMeasure(
                    UnitSymbol ?? string.Empty,
                    (Dimension)UnitDimension.Value,
                    UnitFactorToSi.Value,
                    UnitOffsetToSi ?? 0.0),
            AddressTemplate = AddressTemplate,
            IsWritable = IsWritable,
        };
    }

    internal sealed record AlarmDefinitionRow(Guid Id, Guid TagId, double? HighLimit, double? LowLimit)
    {
        internal AlarmDefinition ToDomain() => new()
        {
            Id = Id,
            TagId = TagId,
            HighLimit = HighLimit,
            LowLimit = LowLimit,
        };
    }

    internal sealed record TagRow(
        Guid Id,
        Guid DeviceId,
        string Name,
        short ValueKind,
        string? UnitSymbol,
        short? UnitDimension,
        double? UnitFactorToSi,
        double? UnitOffsetToSi,
        string SourceAddress,
        bool IsWritable,
        Guid? TemplateTagId)
    {
        internal Tag ToDomain() => new()
        {
            Id = Id,
            DeviceId = DeviceId,
            Name = Name,
            ValueKind = (TagValueKind)ValueKind,
            // A bare symbol is not a unit (ADR-0005): without a dimension and a factor
            // there is nothing to convert with, so the unit is absent rather than partial.
            Unit = UnitDimension is null || UnitFactorToSi is null
                ? null
                : new UnitOfMeasure(
                    UnitSymbol ?? string.Empty,
                    (Dimension)UnitDimension.Value,
                    UnitFactorToSi.Value,
                    UnitOffsetToSi ?? 0.0),
            SourceAddress = SourceAddress,
            IsWritable = IsWritable,
            TemplateTagId = TemplateTagId,
        };
    }
}
