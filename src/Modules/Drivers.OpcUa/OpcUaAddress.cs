using Opc.Ua;

namespace ScadaDarbox.Modules.Drivers.OpcUa;

/// <summary>
/// A parsed <c>Tag.SourceAddress</c> for this driver: an OPC UA NodeId such as
/// <c>ns=2;s=Pump1.Pressure</c>.
/// </summary>
/// <remarks>
/// The format is the protocol's own, interpreted only here — core treats the address as
/// an opaque string and carries no knowledge of OPC UA (ADR-0002).
/// </remarks>
public static class OpcUaAddress
{
    /// <summary>Parses a node identifier, or explains why it is not one.</summary>
    public static bool TryParse(string sourceAddress, out NodeId nodeId, out string error)
    {
        nodeId = NodeId.Null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceAddress))
        {
            error = "An OPC UA address cannot be empty.";
            return false;
        }

        try
        {
            nodeId = NodeId.Parse(sourceAddress);
        }
        // ArgumentException belongs here as much as the other two: NodeId.Parse throws it
        // for an identifier it cannot make sense of, and letting that escape would abort
        // the whole device scan over one mistyped address — the opposite of what this
        // method exists to do. A test caught exactly that.
        catch (Exception exception)
            when (exception is ServiceResultException or FormatException or ArgumentException)
        {
            error = $"'{sourceAddress}' is not an OPC UA node id — expected something like 'ns=2;s=Pump1.Pressure'.";
            return false;
        }

        if (NodeId.IsNull(nodeId))
        {
            error = $"'{sourceAddress}' parses to a null node id.";
            return false;
        }

        return true;
    }
}
