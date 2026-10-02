using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Provisioning;

/// <summary>
/// Derives one edge's configuration from the catalogue the Gateway is running (ADR-0019 §4): the
/// devices assigned to that edge, each with the driver that reads it, the settings the cloud
/// holds, and every tag's plant address — and, for each tag, the Gateway's own tag id.
/// </summary>
/// <remarks>
/// <para>
/// The tag id is the point of the whole message. Until now the same id had to be typed into the
/// edge's own file as well as into the cloud, and a mismatch showed up as a value filed under
/// nothing rather than as an error (ADR-0019 §7). Here it is read from the catalogue, so it
/// cannot disagree.
/// </para>
/// <para>
/// Derived from the catalogue and not from the database: the catalogue is what the Gateway is
/// reading right now, so a configuration derived from it cannot describe a device the Gateway is
/// not polling or an address the catalogue does not hold.
/// </para>
/// <para>
/// The link device is deliberately absent. It is the Gateway's own device, subscribing to what
/// this edge publishes (ADR-0016); the edge neither reads it nor needs to know it exists.
/// </para>
/// </remarks>
public static class EdgeConfigurationBuilder
{
    /// <summary>
    /// What one edge reads, as it will travel to that edge. An edge with nothing assigned
    /// derives an empty list, which is a configuration in its own right: it reads nothing.
    /// </summary>
    /// <remarks>
    /// A device with no tags is left out, and named in <paramref name="omitted"/> (ADR-0020).
    /// The payload reader refuses a device whose tag array is empty and refuses the whole
    /// message with it, so deriving one would take every other device that edge reads down
    /// with it — the cloud would be producing a message its own reader cannot read. Such a
    /// device is a legitimate state rather than a fault: an operator creates a device, assigns
    /// it, and adds its tags next, and the catalog's reload republishes the configuration the
    /// moment the first tag is saved. The list is reported so the caller can say so, because a
    /// device that is assigned and silently unread is exactly the shape of finding ADR-0019 §8
    /// closed on the other axis.
    /// </remarks>
    public static IReadOnlyList<EdgeConfigurationDevice> DevicesFor(
        TagCatalog catalog,
        Edge edge,
        out IReadOnlyList<string> omitted)
    {
        var devices = new List<EdgeConfigurationDevice>();
        var withoutTags = new List<string>();

        foreach (var device in catalog.DevicesOfEdge(edge.Id))
        {
            var tags = catalog.TagsOfDevice(device.Id)
                .Select(tag => new EdgeConfigurationTag(tag.Id, tag.SourceAddress, tag.ValueKind))
                .ToList();

            if (tags.Count == 0)
            {
                // Not sent. A device with no tags is not a device the edge can read, and one
                // empty tag array would have the edge refuse the whole configuration.
                withoutTags.Add(device.Name);
                continue;
            }

            devices.Add(new EdgeConfigurationDevice(
                device.Name,
                device.DriverKey,
                (int)device.ScanInterval.TotalMilliseconds,
                device.ConnectionSettings,
                tags));
        }

        omitted = withoutTags;
        return devices;
    }
}
