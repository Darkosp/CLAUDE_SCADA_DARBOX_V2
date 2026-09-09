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
    internal static SiteTreeDto? Build(TagCatalog catalog, Guid siteId)
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
        // that guarantee explicit instead of leaving it to the shape of the data.
        var visited = new HashSet<Guid>();

        return new SiteTreeDto(
            site.Id,
            site.Name,
            site.TimeZoneId,
            BuildFolders(Guid.Empty, foldersByParent, devicesByFolder, catalog, visited),
            BuildDevices(Guid.Empty, devicesByFolder, catalog));
    }

    private static List<TreeFolderDto> BuildFolders(
        Guid parentKey,
        Dictionary<Guid, List<Folder>> foldersByParent,
        Dictionary<Guid, List<Device>> devicesByFolder,
        TagCatalog catalog,
        HashSet<Guid> visited)
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
                BuildFolders(folder.Id, foldersByParent, devicesByFolder, catalog, visited),
                BuildDevices(folder.Id, devicesByFolder, catalog)));
        }

        return result;
    }

    private static List<TreeDeviceDto> BuildDevices(
        Guid folderKey,
        Dictionary<Guid, List<Device>> devicesByFolder,
        TagCatalog catalog) =>
        devicesByFolder.TryGetValue(folderKey, out var devices)
            ? devices.Select(device => ToDto(device, catalog)).ToList()
            : [];

    internal static TreeDeviceDto ToDto(Device device, TagCatalog catalog) => new(
        device.Id,
        device.Name,
        device.DriverKey,
        device.ConnectionSettings,
        (int)device.ScanInterval.TotalMilliseconds,
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
