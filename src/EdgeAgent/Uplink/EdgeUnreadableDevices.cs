using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Uplink;

/// <summary>
/// The devices this edge has been assigned and cannot read, as the last configuration it accepted
/// left them (ADR-0021).
/// </summary>
/// <remarks>
/// <para>
/// The one piece of state acquisition and the uplink have to share. Acquisition is what finds out —
/// it iterates the accepted configuration and asks each device for a driver factory it may not have
/// — and the uplink is what says so, on the declaration it already publishes. Neither can own it:
/// making acquisition publish would give it an MQTT client, and making the uplink read the
/// configuration would duplicate the check that produces the answer.
/// </para>
/// <para>
/// Replaced whole rather than appended to, because a configuration that has been accepted describes
/// every device the edge has: a device that is readable again leaves the set by not being named in
/// the new one, which is what makes "a missing driver restored" need no separate message.
/// </para>
/// </remarks>
public sealed class EdgeUnreadableDevices
{
    private volatile IReadOnlyList<EdgeUnreadableDevice> _devices = [];

    /// <summary>The devices, in the order the last accepted configuration named them.</summary>
    public IReadOnlyList<EdgeUnreadableDevice> Current => _devices;

    /// <summary>
    /// Replaces the set, and says whether it differs from what was there. The caller uses that to
    /// decide whether a declaration has to be sent: an unchanged set is a declaration the cloud
    /// already has.
    /// </summary>
    public bool Replace(IReadOnlyList<EdgeUnreadableDevice> devices)
    {
        var previous = _devices;
        _devices = devices;

        return !Same(previous, devices);
    }

    private static bool Same(
        IReadOnlyList<EdgeUnreadableDevice> left,
        IReadOnlyList<EdgeUnreadableDevice> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            // The name identifies the device to the cloud, and the driver is what the edge is
            // saying it lacks: a device whose needed driver changed is a different report.
            if (!string.Equals(left[index].Device, right[index].Device, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[index].Driver, right[index].Driver, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
