namespace ScadaDarbox.Core.Model;

/// <summary>
/// A physical installation, and the mandatory root of the browse hierarchy
/// (ADR-0001). A first-class entity with its own identity and time zone, not a
/// folder-name convention, so that generic tooling can be written against it.
/// </summary>
public sealed class Site
{
    public required Guid Id { get; init; }

    /// <summary>Owning tenant (ADR-0004).</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Display name. Mutable — renaming a site never changes its identity.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// IANA time zone of the installation, e.g. <c>Europe/Skopje</c>. Values are stored
    /// and processed in UTC throughout; this is used only to render local time.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";
}
