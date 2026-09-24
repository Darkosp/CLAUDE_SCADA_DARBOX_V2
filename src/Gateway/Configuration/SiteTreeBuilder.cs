using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Shapes one site's slice of the catalogue into the nested tree the browse UI renders.
/// </summary>
/// <remarks>
/// Built from the in-memory catalogue rather than by querying, so the whole tree costs
/// no database round trips. The phase plan settled on one eager response per site rather
/// than lazy per-node fetches; a lazy endpoint would be added alongside this one, never
/// instead of it.
/// </remarks>
internal static class SiteTreeBuilder
{
    /// <param name="shapes">Which drivers push; their devices show no scan interval (ADR-0016).</param>
    internal static SiteTreeDto? Build(TagCatalog catalog, Guid siteId, DriverShapes? shapes = null)
    {
        var site = catalog.Sites.FirstOrDefault(s => s.Id == siteId);
        if (site is null)
        {
            return null;
        }

        var folders = catalog.Folders.Where(f => f.SiteId == siteId).ToList();
        var devices = catalog.Devices.Where(d => d.SiteId == siteId).ToList();

        var foldersByParent = folders
            .GroupBy(f => f.ParentFolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var devicesByFolder = devices
            .GroupBy(d => d.FolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList());

        // Descending from the roots means a folder caught in a parent cycle is simply
        // never reached: unreachable rather than an infinite walk. The visited set makes
        // that guarantee explicit instead of leaving it to the shape of the data, and
        // doubles as the record of which folders the tree actually rendered.
        var visited = new HashSet<Guid>();
        var foldersRendered = BuildFolders(Guid.Empty, foldersByParent, devicesByFolder, catalog, visited, shapes);

        // A device whose folder never appeared — deleted underneath it, or unreachable
        // through a cycle — would otherwise be grouped under a key nothing emits and
        // vanish from the tree while still reporting values in the live tag list. It is
        // shown at the site root instead: misplaced is recoverable, invisible is not.
        var rootDevices = devices
            .Where(device => device.FolderId is null || !visited.Contains(device.FolderId.Value))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(device => ToDto(device, catalog, shapes))
            .ToList();

        return new SiteTreeDto(site.Id, site.Name, site.TimeZoneId, foldersRendered, rootDevices);
    }

    private static List<TreeFolderDto> BuildFolders(
        Guid parentKey,
        Dictionary<Guid, List<Folder>> foldersByParent,
        Dictionary<Guid, List<Device>> devicesByFolder,
        TagCatalog catalog,
        HashSet<Guid> visited,
        DriverShapes? shapes)
    {
        if (!foldersByParent.TryGetValue(parentKey, out var children))
        {
            return [];
        }

        var result = new List<TreeFolderDto>(children.Count);

        foreach (var folder in children)
        {
            if (!visited.Add(folder.Id))
            {
                continue;
            }

            result.Add(new TreeFolderDto(
                folder.Id,
                folder.Name,
                folder.ParentFolderId,
                BuildFolders(folder.Id, foldersByParent, devicesByFolder, catalog, visited, shapes),
                BuildDevices(folder.Id, devicesByFolder, catalog, shapes)));
        }

        return result;
    }

    private static List<TreeDeviceDto> BuildDevices(
        Guid folderKey,
        Dictionary<Guid, List<Device>> devicesByFolder,
        TagCatalog catalog,
        DriverShapes? shapes) =>
        devicesByFolder.TryGetValue(folderKey, out var devices)
            ? devices.Select(device => ToDto(device, catalog, shapes)).ToList()
            : [];

    /// <param name="shapes">
    /// Which drivers push; a pushing device has no scan interval to show (ADR-0016). Without it,
    /// every device is shown as polled.
    /// </param>
    internal static TreeDeviceDto ToDto(Device device, TagCatalog catalog, DriverShapes? shapes = null) => new(
        device.Id,
        device.Name,
        device.DriverKey,
        device.ConnectionSettings,
        shapes?.Pushes(device.DriverKey) is true ? null : (int)device.ScanInterval.TotalMilliseconds,
        device.FolderId,
        catalog.TagsOfDevice(device.Id)
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList());

    internal static TreeTagDto ToDto(Tag tag) => new(
        tag.Id,
        tag.Name,
        tag.ValueKind.ToString(),
        UnitDto.From(tag.Unit),
        tag.SourceAddress,
        tag.IsWritable);
}
