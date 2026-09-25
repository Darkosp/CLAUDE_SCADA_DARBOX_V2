using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Tools.OpcUaSimulator;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0018's criterion that OPC UA works at the edge — proved by running it against a real
/// server, the reason the edge is not Native AOT — and that acquisition carries on with the link
/// down, into the buffer.
/// </summary>
public sealed class AcquisitionTests : IAsyncLifetime
{
    private static readonly Guid Pressure = new("88888888-8888-4888-8888-888888888801");
    private static readonly Guid Unreachable = new("88888888-8888-4888-8888-888888888802");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");
    private readonly int _port = FreePort();
    private SimulatorServer _server = null!;

    public async Task InitializeAsync() => _server = await SimulatorServer.StartAsync(_port, CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_edge_reads_an_OPC_UA_server_and_an_unreachable_device_into_its_buffer()
    {
        var clockBefore = DateTime.UtcNow;
        _server.Pumps.Publish(4.75, true, DateTime.UtcNow.AddMinutes(-3));

        using var buffer = SampleBuffer.Open(_path, maxPending: 10_000);
        using var acquisition = new AcquisitionService(
            Options.Create(new EdgeOptions
            {
                Id = "plant-7",
                Devices =
                [
                    new EdgeDeviceOptions
                    {
                        Name = "Pump skid",
                        Driver = "opc-ua",
                        ScanIntervalMs = 200,
                        Settings = new()
                        {
                            ["endpointUrl"] = $"opc.tcp://localhost:{_port.ToString(CultureInfo.InvariantCulture)}/ScadaDarboxSimulator",
                            ["acceptUntrustedCertificates"] = "true",
                        },
                        Tags = [new EdgeTagOptions { Id = Pressure, Address = "ns=2;s=Pump1.Pressure", Kind = TagValueKind.Numeric }],
                    },
                    new EdgeDeviceOptions
                    {
                        Name = "Dead PLC",
                        Driver = "modbus-tcp",
                        ScanIntervalMs = 200,
                        Settings = new() { ["host"] = "127.0.0.1", ["port"] = FreePort().ToString(CultureInfo.InvariantCulture) },
                        Tags = [new EdgeTagOptions { Id = Unreachable, Address = "holding:0", Kind = TagValueKind.Numeric }],
                    },
                ],
            }),
            buffer,
            new IDeviceDriverFactory[] { new OpcUaDriverFactory(TimeProvider.System), new ModbusTcpDriverFactory(TimeProvider.System) },
            NullLogger<AcquisitionService>.Instance);

        await acquisition.StartAsync(CancellationToken.None);

        IReadOnlyList<BufferedSample> waiting = [];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(waiting = buffer.Peek(int.MaxValue)).Any(s => s.Reading.TagId == Pressure && s.Reading.Quality == Quality.Good)
               || !waiting.Any(s => s.Reading.TagId == Unreachable))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for both devices to reach the buffer.");
            await Task.Delay(200);
        }

        await acquisition.StopAsync(CancellationToken.None);

        // OPC UA, through the same module as the Gateway: the value and the server's own time.
        var read = waiting.First(s => s.Reading.TagId == Pressure && s.Reading.Quality == Quality.Good).Reading;
        Assert.Equal(new TagValue.Numeric(4.75), read.Value);
        Assert.True(read.SourceTimestampUtc < clockBefore, "the source time is the server's, not the time of the read");

        // The unreachable device is buffered as Bad, with no value — not skipped, not zero.
        Assert.All(waiting.Where(s => s.Reading.TagId == Unreachable), s =>
        {
            Assert.Equal(Quality.Bad, s.Reading.Quality);
            Assert.Null(s.Reading.Value);
        });
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
