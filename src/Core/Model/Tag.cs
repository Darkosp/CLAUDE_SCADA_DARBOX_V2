namespace ScadaDarbox.Core.Model;

/// <summary>
/// A single signal. Its identity is <see cref="Id"/> — a stable, immutable UUID
/// assigned at creation (ADR-0001). Every internal reference (historian rows,
/// alarm configuration, screen bindings) uses that ID; the human-visible path is
/// a derived display label that may be renamed freely without consequence.
/// </summary>
public sealed class Tag
{
    /// <summary>Stable identity. Never derived from, and never affected by, the display path.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning device, and through it the site and tenant.</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>Display name. Mutable; renaming does not break historian continuity.</summary>
    public required string Name { get; set; }

    /// <summary>Which <see cref="TagValue"/> kind this tag produces (ADR-0003).</summary>
    public required TagValueKind ValueKind { get; init; }

    /// <summary>
    /// Unit of measure, as a dimensioned record (ADR-0005). Null for tags whose value
    /// has no physical dimension — booleans, text and discrete states.
    /// </summary>
    public UnitOfMeasure? Unit { get; set; }

    /// <summary>
    /// Where the driver finds this value on the device, e.g. a Modbus register address.
    /// Opaque to core; only the owning device's driver interprets it (ADR-0002).
    /// </summary>
    public required string SourceAddress { get; init; }

    /// <summary>Whether an operator may write this tag back to the device.</summary>
    public bool IsWritable { get; init; }

    /// <summary>
    /// The template tag this one was materialised from, or null for a tag created
    /// directly on its device (ADR-0010).
    /// </summary>
    /// <remarks>
    /// This is what lets a later template edit find the rows it has to change. The tag
    /// itself is ordinary in every other respect — its own stable ID, its own history.
    /// </remarks>
    public Guid? TemplateTagId { get; init; }
}
