namespace ScadaDarbox.Core.Model;

/// <summary>
/// The customer an instance serves (ADR-0004). Every deployment contains exactly
/// one tenant row, by deployment convention rather than by omitting the concept.
/// Tenant sits above Site so that every site-scoped entity is transitively
/// tenant-scoped, and it never appears in the human-visible tag path.
/// </summary>
public sealed class Tenant
{
    public required Guid Id { get; init; }

    public required string Name { get; set; }
}
