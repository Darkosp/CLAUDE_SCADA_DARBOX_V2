namespace ScadaDarbox.Core.Security;

/// <summary>
/// What one active user may currently see and do, resolved from stored roles.
/// </summary>
/// <remarks>
/// Never carried by a session token (ADR-0011): a token resolves to a user id only, and
/// this is looked up afresh for every request, so a revoked role stops working on the
/// very next one.
/// </remarks>
public sealed record UserAccess(
    Guid UserId,
    string Username,
    bool IsAdmin,
    IReadOnlyDictionary<Guid, SiteRole> SiteRoles)
{
    /// <summary>Whether the user may read data belonging to <paramref name="siteId"/>.</summary>
    public bool CanView(Guid siteId) => IsAdmin || SiteRoles.ContainsKey(siteId);

    /// <summary>
    /// Whether the user may take operational action — write a tag, acknowledge or shelve
    /// an alarm — within <paramref name="siteId"/>.
    /// </summary>
    public bool CanOperate(Guid siteId) =>
        IsAdmin || (SiteRoles.TryGetValue(siteId, out var role) && role == SiteRole.Operator);

    /// <summary>
    /// Whether the user may see something whose Site can no longer be resolved — a
    /// standing alarm on a tag deleted since it was raised, say.
    /// </summary>
    /// <remarks>
    /// Only Admin, which is unrestricted. For anyone else "no Site" has to mean "not
    /// yours": treating it as "everyone's" would turn a deletion into a disclosure.
    /// </remarks>
    public bool CanViewUnscoped => IsAdmin;
}
