using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// The current state of one tag, as handed to clients.
/// </summary>
/// <param name="TagId">Stable identity (ADR-0001). Clients bind to this, never to <paramref name="Path"/>.</param>
/// <param name="Path">
/// Derived display label, composed from the site, device and tag names. Mutable and
/// presentational: renaming anything along it changes this string and nothing else.
/// The tenant is deliberately absent from it (ADR-0004).
/// </param>
/// <param name="UnitSymbol">
/// Display symbol derived from the tag's dimensioned unit (ADR-0005), or null where
/// the value has no physical dimension.
/// </param>
public sealed record TagSnapshot(
    Guid TagId,
    string Path,
    TagValue? Value,
    DateTimeOffset SourceTimestampUtc,
    Quality Quality,
    string? UnitSymbol);
