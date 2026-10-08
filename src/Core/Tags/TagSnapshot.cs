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
/// <param name="SourceTimestampUtc">
/// When the source measured the value (ADR-0003). Null only when nothing has ever been
/// measured for this tag — a pushing tag that has received nothing at all — because then
/// there is no measurement to take a time from, and a time nobody measured must not be made up.
/// </param>
/// <param name="UnitSymbol">
/// Display symbol derived from the tag's dimensioned unit (ADR-0005), or null where
/// the value has no physical dimension.
/// </param>
/// <param name="NoDataSinceUtc">
/// Set only on a tag that has never received anything: when the Gateway began listening for it.
/// An observed time, not a measured one — the same distinction ADR-0013 draws for a clear first
/// seen after a restart — so a screen can say "no data since", and tell a device that was never
/// set up from one that fell silent.
/// </param>
public sealed record TagSnapshot(
    Guid TagId,
    string Path,
    TagValue? Value,
    DateTimeOffset? SourceTimestampUtc,
    Quality Quality,
    string? UnitSymbol,
    DateTimeOffset? NoDataSinceUtc = null,
    TagRange? Range = null);
