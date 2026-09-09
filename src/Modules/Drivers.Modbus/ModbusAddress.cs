using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Modbus;

/// <summary>The four Modbus data areas a tag can be read from.</summary>
public enum ModbusArea
{
    /// <summary>Read/write 16-bit register.</summary>
    Holding,

    /// <summary>Read-only 16-bit register.</summary>
    Input,

    /// <summary>Read/write single bit.</summary>
    Coil,

    /// <summary>Read-only single bit.</summary>
    Discrete,
}

/// <summary>
/// A parsed <see cref="Tag.SourceAddress"/> for this driver, in the form
/// <c>area:offset</c> with an optional <c>?scale=</c>, e.g. <c>holding:0?scale=0.01</c>.
/// </summary>
/// <remarks>
/// The format is defined and interpreted only by this module — core treats the address
/// as an opaque string and carries no knowledge of Modbus (ADR-0002). The scale exists
/// because a Modbus register is a raw 16-bit integer with no engineering meaning of its
/// own: the device's documentation states what it represents, and that mapping belongs
/// to the device's address, not to the platform's tag model.
/// </remarks>
public sealed record ModbusAddress(ModbusArea Area, ushort Offset, double Scale = 1.0)
{
    public static ModbusAddress Parse(string sourceAddress)
    {
        if (!TryParse(sourceAddress, out var address, out var error))
        {
            throw new FormatException(error);
        }

        return address;
    }

    public static bool TryParse(string sourceAddress, out ModbusAddress address, out string error)
    {
        address = default!;
        error = string.Empty;

        var withoutQuery = sourceAddress;
        var scale = 1.0;

        var queryStart = sourceAddress.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            withoutQuery = sourceAddress[..queryStart];
            var query = sourceAddress[(queryStart + 1)..];

            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                {
                    error = $"Malformed option '{part}' in Modbus address '{sourceAddress}'.";
                    return false;
                }

                var key = part[..separator];
                var rawValue = part[(separator + 1)..];

                if (!key.Equals("scale", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Unknown option '{key}' in Modbus address '{sourceAddress}'.";
                    return false;
                }

                if (!double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out scale))
                {
                    error = $"Option 'scale' is not a number in Modbus address '{sourceAddress}'.";
                    return false;
                }
            }
        }

        var colon = withoutQuery.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            error = $"Modbus address '{sourceAddress}' is not in the form 'area:offset'.";
            return false;
        }

        if (!Enum.TryParse<ModbusArea>(withoutQuery[..colon], ignoreCase: true, out var area))
        {
            error = $"Unknown Modbus area '{withoutQuery[..colon]}' — expected holding, input, coil or discrete.";
            return false;
        }

        if (!ushort.TryParse(withoutQuery[(colon + 1)..], out var offset))
        {
            error = $"Modbus offset '{withoutQuery[(colon + 1)..]}' is not a 16-bit unsigned integer.";
            return false;
        }

        address = new ModbusAddress(area, offset, scale);
        return true;
    }

    /// <summary>Whether this area yields a single bit rather than a 16-bit register.</summary>
    public bool IsBitArea => Area is ModbusArea.Coil or ModbusArea.Discrete;
}
