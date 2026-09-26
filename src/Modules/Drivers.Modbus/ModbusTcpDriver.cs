using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NModbus;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Modbus;

/// <summary>
/// Reads and writes tags over Modbus TCP. A module: it depends only on core's public
/// contracts and is composed at compile time, never discovered by reflection (ADR-0002).
/// </summary>
/// <remarks>
/// An unreachable device, a mistyped address and a value the protocol cannot carry all end
/// as the same reading with no value, which is what ADR-0003 asks for — and all three are
/// written to the log with their own reason, so that the same reading is not all anyone can
/// see of them.
/// </remarks>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>The reason each tag last read Bad, so a standing fault is named once.</summary>
    /// <remarks>
    /// A fault that repeats every scan is one condition, not an event per second: writing it
    /// again each time buries the line that says what is actually wrong — the same reasoning
    /// the alarm journal uses for a flapping value. The entry is dropped when the tag reads
    /// again, so a fault that returns is named again, and a reason that changes is a new
    /// message rather than one swallowed by the first.
    /// <para>
    /// No lock: a device's tags are read by that device's own scan loop, one at a time, and
    /// nothing else touches this. Writes do not.
    /// </para>
    /// </remarks>
    private readonly Dictionary<Guid, string> _badReasons = [];

    /// <summary>How long a request waits for the device before it is given up on.</summary>
    /// <remarks>
    /// A socket read with no timeout waits forever, and that is the default: a device that
    /// accepts the connection and then stops answering — a wedged gateway, a firewall that
    /// takes the connection and drops what follows — used to leave the read hanging, so the
    /// scan loop never reached the next tag and the tags already read kept the values they
    /// had. Silence that looks like a live plant is the reading ADR-0003 refuses. Bounding
    /// the request is what turns it into a Bad reading with the runtime's own words beside it.
    /// </remarks>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public ModbusTcpDriver(
        string host,
        int port,
        byte unitId,
        TimeProvider timeProvider,
        ILogger<ModbusTcpDriver>? logger = null)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
        _timeProvider = timeProvider;

        // Optional so a composition that deliberately keeps no log — a unit test, a tool —
        // needs no argument, and the same way the MQTT module takes its logger.
        _logger = logger ?? NullLogger<ModbusTcpDriver>.Instance;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

        _tcpClient = client;
        _master = new ModbusFactory().CreateMaster(client);

        // Bounded so a device that stops answering reads Bad instead of holding the scan loop
        // (see ResponseTimeout). The transport carries the setting to the socket underneath.
        _master.Transport.ReadTimeout = (int)ResponseTimeout.TotalMilliseconds;
        _master.Transport.WriteTimeout = (int)ResponseTimeout.TotalMilliseconds;
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
        if (!ModbusAddress.TryParse(tag.SourceAddress, out var address, out var error))
        {
            // A misconfigured address is a permanent fault, not a transient one, but it is
            // still reported as a value with Bad quality rather than thrown: one broken tag
            // must not stop the rest of the device from being scanned. The parser's own words
            // go to the log with it — otherwise a mistyped address and a device that will not
            // answer read identically from the outside.
            return Bad(tag, error);
        }

        if (_master is null)
        {
            return Bad(tag, "the connection has not succeeded yet");
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

            var reading = new TagReading(tag.TagId, value, _timeProvider.GetUtcNow(), Quality.Good);
            ReportRecovery(tag);
            return reading;
        }
        catch (Exception exception) when (exception is SlaveException or IOException or SocketException
                                              or TimeoutException or InvalidOperationException)
        {
            // The device answered with an error, or the link is down. Either way there is no
            // value: reporting Bad is what lets the rest of the system tell this apart from a
            // genuine zero (ADR-0003). Which of the two it was goes to the log with it.
            return Bad(tag, exception);
        }
    }

    /// <remarks>
    /// No value is supplied — not a zero, not a false, not an empty string. A fabricated
    /// placeholder is indistinguishable from a real reading of the same shape, which is
    /// precisely the confusion ADR-0003 exists to prevent.
    /// </remarks>
    private TagReading Bad(DriverTag tag, string reason)
    {
        ReportFault(tag, reason);
        return new(tag.TagId, null, _timeProvider.GetUtcNow(), Quality.Bad);
    }

    /// <summary>The same reading, for a failure the runtime described itself.</summary>
    private TagReading Bad(DriverTag tag, Exception exception)
    {
        // The exception is carried as well as its message: the message is the reason an
        // operator reads, and the stack is what is needed when it is not self-explanatory.
        ReportFault(tag, exception.Message, exception);
        return new(tag.TagId, null, _timeProvider.GetUtcNow(), Quality.Bad);
    }

    private void ReportFault(DriverTag tag, string reason, Exception? exception = null)
    {
        if (_badReasons.TryGetValue(tag.TagId, out var previous) && previous == reason)
        {
            // Already said, and nothing about it has changed.
            return;
        }

        _badReasons[tag.TagId] = reason;

        _logger.LogWarning(
            exception,
            "Tag {TagId} (address '{SourceAddress}') reads Bad on {Host}:{Port}: {Reason}",
            tag.TagId,
            tag.SourceAddress,
            _host,
            _port,
            reason);
    }

    private void ReportRecovery(DriverTag tag)
    {
        // Only for a tag that had a fault to report: an ordinary Good reading is not news.
        if (_badReasons.Remove(tag.TagId))
        {
            _logger.LogInformation(
                "Tag {TagId} (address '{SourceAddress}') on {Host}:{Port} reads Good again.",
                tag.TagId,
                tag.SourceAddress,
                _host,
                _port);
        }
    }

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
