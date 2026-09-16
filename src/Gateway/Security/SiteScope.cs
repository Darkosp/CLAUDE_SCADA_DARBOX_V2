using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Resolving data to its Site, and asking whether a user may see or act on it there
/// (ADR-0011).
/// </summary>
internal static class SiteScope
{
    /// <summary>The Site a live tag belongs to, or null if it is not in the catalogue.</summary>
    public static Guid? SiteOfTag(this TagCatalog catalog, Guid tagId) =>
        catalog.FindTag(tagId) is { } tag && catalog.FindDevice(tag.DeviceId) is { } device
            ? device.SiteId
            : null;

    public static bool CanSeeTag(this UserAccess access, TagCatalog catalog, Guid tagId) =>
        catalog.SiteOfTag(tagId) is { } siteId ? access.CanView(siteId) : access.CanViewUnscoped;

    public static bool CanOperateTag(this UserAccess access, TagCatalog catalog, Guid tagId) =>
        catalog.SiteOfTag(tagId) is { } siteId ? access.CanOperate(siteId) : access.IsAdmin;
}
