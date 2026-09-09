namespace ScadaDarbox.Core.Model;

/// <summary>
/// Quality of a tag value, modelled on OPC UA's quality-code concept (ADR-0003).
/// Every driver maps its native status codes onto this set; a driver must never
/// report <see cref="Good"/> unconditionally.
/// </summary>
public enum Quality
{
    /// <summary>The value is trustworthy and current.</summary>
    Good = 0,

    /// <summary>A value was produced, but something about it is doubtful.</summary>
    Uncertain = 1,

    /// <summary>No usable value — the source is offline, faulted, or unreadable.</summary>
    Bad = 2,

    /// <summary>The last known value, retained past the point where it should have been refreshed.</summary>
    Stale = 3,
}
