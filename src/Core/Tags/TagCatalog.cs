using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// An immutable in-memory projection of the configured hierarchy, used to resolve a
/// tag's identity to its device, its unit, and its display path.
/// </summary>
/// <remarks>
/// The path is computed here from the current entity names — it is never stored
/// alongside a tag and never used as a key (ADR-0001). Rebuilding this catalog after a
/// rename changes only what the operator sees.
/// </remarks>
public sealed class TagCatalog
{
    private readonly Dictionary<Guid, Tag> _tagsById;
    private readonly Dictionary<Guid, Device> _devicesById;
    private readonly Dictionary<Guid, Site> _sitesById;
    private readonly Dictionary<Guid, string> _pathsByTagId;

    public TagCatalog(
        Tenant tenant,
        IReadOnlyList<Site> sites,
        IReadOnlyList<Device> devices,
        IReadOnlyList<Tag> tags)
    {
        Tenant = tenant;
        _sitesById = sites.ToDictionary(s => s.Id);
        _devicesById = devices.ToDictionary(d => d.Id);
        _tagsById = tags.ToDictionary(t => t.Id);

        _pathsByTagId = new Dictionary<Guid, string>(tags.Count);
        foreach (var tag in tags)
        {
            _pathsByTagId[tag.Id] = BuildPath(tag);
        }
    }

    public Tenant Tenant { get; }

    public IReadOnlyCollection<Tag> Tags => _tagsById.Values;

    public IReadOnlyCollection<Device> Devices => _devicesById.Values;

    public Tag? FindTag(Guid tagId) => _tagsById.GetValueOrDefault(tagId);

    public Device? FindDevice(Guid deviceId) => _devicesById.GetValueOrDefault(deviceId);

    /// <summary>Tags belonging to one device, in configuration order.</summary>
    public IReadOnlyList<Tag> TagsOfDevice(Guid deviceId) =>
        _tagsById.Values.Where(t => t.DeviceId == deviceId).ToList();

    /// <summary>
    /// The derived display path for a tag, e.g. <c>Skopje/Pump House/Discharge Pressure</c>.
    /// Presentation only.
    /// </summary>
    public string PathOf(Guid tagId) => _pathsByTagId.GetValueOrDefault(tagId, "<unknown>");

    private string BuildPath(Tag tag)
    {
        // Site is the mandatory root of the hierarchy (ADR-0001). Free-form folders
        // between site and device are not yet modelled — the browse tree arrives in
        // Phase 2 — and slot in here when they do, without affecting tag identity.
        if (!_devicesById.TryGetValue(tag.DeviceId, out var device))
        {
            return tag.Name;
        }

        return _sitesById.TryGetValue(device.SiteId, out var site)
            ? $"{site.Name}/{device.Name}/{tag.Name}"
            : $"{device.Name}/{tag.Name}";
    }
}
