using System.Net.Sockets;
using NModbus;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Modbus;

/// <summary>
/// Reads and writes tags over Modbus TCP. A module: it depends only on core's public
/// contracts and is composed at compile time, never discovered by reflection (ADR-0002).
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly TimeProvider _timeProvider;

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public ModbusTcpDriver(string host, int port, byte unitId, TimeProvider timeProvider)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
        _timeProvider = timeProvider;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

        _tcpClient = client;
        _master = new ModbusFactory().CreateMaster(client);
    }

    /// <remarks>
    /// Tags are read one per request. Phase 1 proves the path end-to-end rather than
    /// optimising it; batching contiguous ranges into single requests is worth doing once
    /// a device with a large tag count actually exists.
    /// </remarks>
    public async Task<IReadOnlyList<TagReading>> ReadAsync(
        IReadOnlyList<DriverTag> tags,
        CancellationToken cancellationToken)
    {
        var readings = new List<TagReading>(tags.Count);

        foreach (var tag in tags)
        {
            readings.Add(await ReadOneAsync(tag, cancellationToken).ConfigureAwait(false));
        }

        return readings;
    }

    public async Task WriteAsync(DriverTag tag, TagValue value, CancellationToken cancellationToken)
    {
        if (_master is null)
        {
            throw new InvalidOperationException("Cannot write before ConnectAsync has succeeded.");
        }

        var address = ModbusAddress.Parse(tag.SourceAddress);

        switch (value)
        {
            case TagValue.Boolean b when address.IsBitArea:
                await _master.WriteSingleCoilAsync(_unitId, address.Offset, b.Value).ConfigureAwait(false);
                break;

            case TagValue.Numeric n when !address.IsBitArea:
                var raw = (ushort)Math.Round(n.Value / address.Scale, MidpointRounding.AwayFromZero);
                await _master.WriteSingleRegisterAsync(_unitId, address.Offset, raw).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException(
                    $"Cannot write a {value.Kind} value to Modbus area {address.Area}.");
        }
    }

    private async Task<TagReading> ReadOneAsync(DriverTag tag, CancellationToken cancellationToken)
    {
        if (!ModbusAddress.TryParse(tag.SourceAddress, out var address, out _))
        {
            // A misconfigured address is a permanent fault, not a transient one, but it is
            // still reported as a value with Bad quality rather than thrown: one broken tag
            // must not stop the rest of the device from being scanned.
            return Bad(tag);
        }

        if (_master is null)
        {
            return Bad(tag);
        }

        try
        {
            // Modbus carries no device-side capture timestamp. The instant the read
            // completed is the closest the protocol gets to one, and it is recorded as the
            // source timestamp deliberately — not silently equated with the later ingestion
            // timestamp the tag engine stamps (ADR-0003).
            TagValue value;

            if (address.IsBitArea)
            {
                var bits = address.Area == ModbusArea.Coil
                    ? await _master.ReadCoilsAsync(_unitId, address.Offset, 1).ConfigureAwait(false)
                    : await _master.ReadInputsAsync(_unitId, address.Offset, 1).ConfigureAwait(false);

                value = new TagValue.Boolean(bits[0]);
            }
            else
            {
                var registers = address.Area == ModbusArea.Holding
                    ? await _master.ReadHoldingRegistersAsync(_unitId, address.Offset, 1).ConfigureAwait(false)
                    : await _master.ReadInputRegistersAsync(_unitId, address.Offset, 1).ConfigureAwait(false);

                value = new TagValue.Numeric(registers[0] * address.Scale);
            }

            return new TagReading(tag.TagId, value, _timeProvider.GetUtcNow(), Quality.Good);
        }
        catch (Exception exception) when (exception is SlaveException or IOException or SocketException
                                              or TimeoutException or InvalidOperationException)
        {
            // The device answered with an error, or the link is down. Either way there is no
            // value: reporting Bad is what lets the rest of the system tell this apart from a
            // genuine zero (ADR-0003).
            return Bad(tag);
        }
    }

    private TagReading Bad(DriverTag tag) => new(
        tag.TagId,
        tag.ValueKind switch
        {
            TagValueKind.Boolean => new TagValue.Boolean(false),
            TagValueKind.Text => new TagValue.Text(string.Empty),
            TagValueKind.Discrete => new TagValue.Discrete(0),
            _ => new TagValue.Numeric(double.NaN),
        },
        _timeProvider.GetUtcNow(),
        Quality.Bad);

    public async ValueTask DisposeAsync() => await DisposeConnectionAsync().ConfigureAwait(false);

    private ValueTask DisposeConnectionAsync()
    {
        _master?.Dispose();
        _master = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        return ValueTask.CompletedTask;
    }
}
