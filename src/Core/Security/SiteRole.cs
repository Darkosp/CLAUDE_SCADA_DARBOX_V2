namespace ScadaDarbox.Core.Security;

/// <summary>
/// What a user may do within one Site (ADR-0011).
/// </summary>
/// <remarks>
/// Admin is deliberately not a member. It is tenant-wide rather than Site-scoped, so it
/// lives as a flag on the user; a Site-level "Admin" value would describe a permission the
/// model does not have.
/// </remarks>
public enum SiteRole
{
    /// <summary>Reads tags, alarms and history within the Site. Nothing else.</summary>
    Viewer,

    /// <summary>Viewer, plus writing tag values, acknowledging and shelving alarms.</summary>
    Operator,
}
