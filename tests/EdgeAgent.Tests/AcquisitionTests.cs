using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Tools.OpcUaSimulator;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0018's criterion that OPC UA works at the edge — proved by running it against a real
/// server, the reason the edge is not Native AOT — that acquisition carries on with the link
/// down, into the buffer, and that a newer configuration is applied by restarting it (ADR-0019 §6).
/// </summary>
public sealed class AcquisitionTests : IAsyncLifetime
{
    private static readonly Guid Pressure = new("88888888-8888-4888-8888-888888888801");
    private static readonly Guid Flow = new("88888888-8888-4888-8888-888888888802");
    private static readonly Guid Unreachable = new("88888888-8888-4888-8888-888888888803");

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

        // What the cloud derived for this edge: one device it can reach, one it cannot.
        var configuration = new EdgeConfigurationSource();
        configuration.Replace([Pump(Pressure), DeadPlc()], "sha256:test");

        using var acquisition = Acquisition(configuration, buffer);
        await acquisition.StartAsync(CancellationToken.None);

        IReadOnlyList<BufferedSample> waiting = [];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(waiting = Samples(buffer)).Any(s => s.Reading.TagId == Pressure && s.Reading.Quality == Quality.Good)
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

    [Fact]
    public async Task A_newer_configuration_is_applied_by_restarting_acquisition()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 100_000);
        var configuration = new EdgeConfigurationSource();

        // The first configuration: this device, under the tag id the cloud held then.
        configuration.Replace([Pump(Pressure)], "sha256:first");

        using var acquisition = Acquisition(configuration, buffer);
        await acquisition.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => Samples(buffer).Any(s => s.Reading.TagId == Pressure),
            "the first configuration's tag to reach the buffer");

        // The tag is re-created in the cloud and the device's assignment now names the new id: the
        // edge is sent a configuration whose devices differ only in that (ADR-0019 §7).
        configuration.Replace([Pump(Flow)], "sha256:second");
        await WaitUntilAsync(
            () => Samples(buffer).Any(s => s.Reading.TagId == Flow),
            "the second configuration's tag to reach the buffer");

        var underTheFirst = Samples(buffer).Count(s => s.Reading.TagId == Pressure);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await acquisition.StopAsync(CancellationToken.None);

        // The loop for the configuration that was replaced has stopped — nothing more is read under
        // the tag it named — while the one now in force keeps being read.
        Assert.Equal(underTheFirst, Samples(buffer).Count(s => s.Reading.TagId == Pressure));
        Assert.True(
            Samples(buffer).Count(s => s.Reading.TagId == Flow) > 1,
            "the configuration in force is still being read");
    }

    private AcquisitionService Acquisition(EdgeConfigurationSource configuration, SampleBuffer buffer) => new(
        configuration,
        buffer,
        new IDeviceDriverFactory[] { new OpcUaDriverFactory(TimeProvider.System), new ModbusTcpDriverFactory(TimeProvider.System) },
        NullLogger<AcquisitionService>.Instance);

    private EdgeConfigurationDevice Pump(Guid tagId) => new(
        "Pump skid",
        "opc-ua",
        200,
        new Dictionary<string, string>
        {
            ["endpointUrl"] = $"opc.tcp://localhost:{_port.ToString(CultureInfo.InvariantCulture)}/ScadaDarboxSimulator",
            ["acceptUntrustedCertificates"] = "true",
        },
        [new EdgeConfigurationTag(tagId, "ns=2;s=Pump1.Pressure", TagValueKind.Numeric)]);

    private static EdgeConfigurationDevice DeadPlc() => new(
        "Dead PLC",
        "modbus-tcp",
        200,
        new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = FreePort().ToString(CultureInfo.InvariantCulture),
        },
        [new EdgeConfigurationTag(Unreachable, "holding:0", TagValueKind.Numeric)]);

    private static List<BufferedSample> Samples(SampleBuffer buffer) => buffer.Peek(int.MaxValue).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
