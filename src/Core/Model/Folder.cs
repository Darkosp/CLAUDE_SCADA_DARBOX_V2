namespace ScadaDarbox.Core.Model;

/// <summary>
/// An organisational node in the browse tree (ADR-0001 §4). Folders nest freely
/// below a site, with no fixed depth.
/// </summary>
/// <remarks>
/// A folder carries no functional behaviour (ADR-0001 §6): no driver configuration,
/// no alarm class, no security scope. Moving a device between folders changes where
/// an operator finds it and nothing else.
/// </remarks>
public sealed class Folder
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Owning site. Immutable: a folder cannot be moved to another site, and the
    /// database carries this through the composite key on its parent reference.
    /// </summary>
    public required Guid SiteId { get; init; }

    /// <summary>
    /// Parent folder, or null when the folder sits directly under its site.
    /// A parent always belongs to the same site.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    /// <summary>Display name. Mutable, and part of no identity.</summary>
    public required string Name { get; set; }
}
