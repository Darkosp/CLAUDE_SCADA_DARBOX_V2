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

    /// <summary>
    /// Every column <see cref="AlarmDefinitionRow"/> needs, named once.
    /// </summary>
    /// <remarks>
    /// <b>Two queries read this row and they drifted the moment a column was added.</b> Dapper
    /// materialises by constructor signature, so a SELECT that omits one fails at run time with
    /// <c>A parameterless default constructor or one matching signature … is required</c> — inside a
    /// Gateway that then refuses to start, which is how it was found rather than by reading. Naming
    /// the list here means the next column cannot be added to one query and forgotten in the other.
    /// </remarks>
    internal const string AlarmDefinitionColumns =
        "id, tag_id, high_limit, low_limit, on_delay_seconds, deadband";

    /// <summary>
    /// One alarm threshold row.
    /// </summary>
    /// <param name="OnDelaySeconds">
    /// Seconds as the column stores them, converted to a <see cref="TimeSpan"/> on the way into the
    /// domain (ADR-0025 §2). The column is a number because a clock difference is a number; the
    /// domain type is a duration because that is what it is.
    /// </param>
    internal sealed record AlarmDefinitionRow(
        Guid Id,
        Guid TagId,
        double? HighLimit,
        double? LowLimit,
        double? OnDelaySeconds,
        double? Deadband)
    {
        internal AlarmDefinition ToDomain() => new()
        {
            Id = Id,
            TagId = TagId,
            HighLimit = HighLimit,
            LowLimit = LowLimit,
            OnDelaySeconds = OnDelaySeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            Deadband = Deadband,
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
        Guid? TemplateTagId,
        double? RangeLow,
        double? RangeHigh)
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
            // Both ends or neither (ADR-0030), and migration 0020's paired CHECK refuses a half-declared
            // range as well as the API does — so a row with one end set is a row nothing should have been
            // able to write, and reading it as "nothing declared" is the only honest answer left.
            Range = RangeLow is { } low && RangeHigh is { } high ? new TagRange(low, high) : null,
        };
    }

    internal sealed record ScreenRow(
        Guid Id,
        Guid TenantId,
        Guid SiteId,
        string Name,
        int Position)
    {
        internal Screen ToDomain(IReadOnlyList<ScreenComponent> components) => new()
        {
            Id = Id,
            TenantId = TenantId,
            SiteId = SiteId,
            Name = Name,
            Position = Position,
            Components = components,
        };
    }

    /// <summary>
    /// The columns <see cref="ScreenComponentRow"/> needs beyond the ones a query names itself,
    /// listed once.
    /// </summary>
    /// <remarks>
    /// <b>This is the second time a row in this file has been read by two queries and the column list
    /// has drifted.</b> Dapper materialises by constructor signature, so a SELECT omitting a column
    /// fails at run time — and the component queries are easy to change one at a time. Naming the list
    /// here means the next column cannot be added to one and forgotten in another.
    ///
    /// **Unqualified, so it is only for a query that does not alias the table.** The one query that
    /// joins `screen_active` writes its columns out instead, because it must qualify them — which is
    /// the price of the alias and is worth knowing before adding a third reader.
    /// </remarks>
    internal const string ScreenComponentColumns = "device_id, symbol, states";

    /// <summary>
    /// The columns <see cref="TagRow"/> needs, listed once — **because two queries read that row.**
    /// </summary>
    /// <remarks>
    /// The note above about the component list applies here word for word, and this is the moment it
    /// describes: ADR-0030 adds two columns to a row that <see cref="TagRepository"/> and
    /// <see cref="PostgresConfigurationStore"/> both select. Dapper materialises by constructor
    /// signature, so the second query would have failed at run time — on the scan path, where a tag
    /// catalogue is loaded — with a message about a constructor and nothing about a column.
    ///
    /// **Unqualified, so it is only for a query that does not alias the table.** Neither reader does.
    /// A third reader that joins would have to write its columns out, as the component query with an
    /// alias already does.
    /// </remarks>
    internal const string TagColumns =
        "id, device_id, name, value_kind, unit_symbol, unit_dimension, unit_factor_to_si, "
        + "unit_offset_to_si, source_address, is_writable, template_tag_id, range_low, range_high";


    /// <summary>
    /// One component row.
    /// </summary>
    /// <param name="States">
    /// The symbol's state mapping exactly as the jsonb column holds it — **raw text, converted in
    /// <see cref="ToDomain"/>**.
    /// </param>
    /// <remarks>
    /// Read as a string and deserialised here rather than through a Dapper type handler, because a
    /// handler is process-global state that every Dapper user in this process would have to agree
    /// about. The conversion belongs beside the column it is about, and putting it here gives the
    /// malformed case one place to be handled.
    /// </remarks>
    internal sealed record ScreenComponentRow(
        Guid Id,
        Guid ScreenId,
        int RowIndex,
        int ColumnSpan,
        int Position,
        string Kind,
        string? Title,
        Guid? TagId,
        Guid? DeviceId,
        string? Symbol,
        string? States)
    {
        internal ScreenComponent ToDomain() => new()
        {
            Id = Id,
            ScreenId = ScreenId,
            RowIndex = RowIndex,
            ColumnSpan = ColumnSpan,
            Position = Position,
            Kind = Kind,
            Title = Title,
            TagId = TagId,
            DeviceId = DeviceId,
            Symbol = Symbol,
            States = ReadStates(States),
        };

        /// <summary>
        /// The mapping, or an empty one when the column holds something that is not one.
        /// </summary>
        /// <remarks>
        /// **Empty rather than throwing**, and the choice matters: a malformed mapping becomes a
        /// component that draws `unknown`, which is visible on the screen and fixable by its author
        /// (ADR-0027 §3). Throwing would take down the whole screen — and every other screen on the
        /// Site with it, since they are read in one query — for one bad row. Migration 0019's CHECK
        /// makes this unreachable through the API; it is here for a row that arrived another way.
        /// </remarks>
        private static IReadOnlyList<SymbolState> ReadStates(string? states) =>
            string.IsNullOrWhiteSpace(states)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<SymbolState>>(states) ?? [];
    }
}
