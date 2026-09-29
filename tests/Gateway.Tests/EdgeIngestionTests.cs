using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.EdgeAgent.Uplink;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The cloud side of the edge link (ADR-0017, Phase 7 step 4), end to end: the real edge buffer
/// and uplink, a real broker, and this Gateway with its database. A batch delivered twice is
/// stored once; a window the edge dropped reaches the journal as a stated gap; an edge clock that
/// is off is journalled while its samples keep the times it gave them.
/// </summary>
public sealed class EdgeIngestionTests : IClassFixture<GatewayTestHost>, IAsyncLifetime
{
    private static readonly TimeSpan Wide = TimeSpan.FromHours(2);

    private readonly GatewayTestHost _host;
    private readonly int _port = FreePort();
    private readonly string _bufferPath = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");
    private MqttServer _broker = null!;

    public EdgeIngestionTests(GatewayTestHost host) => _host = host;

    public async Task InitializeAsync()
    {
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(_port)
            .Build());
        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _broker.StopAsync();
        _broker.Dispose();

        foreach (var file in new[] { _bufferPath, _bufferPath + "-wal", _bufferPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    [RequiresDatabaseFact]
    public async Task The_same_batch_delivered_twice_is_stored_once()
    {
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());
        var edge = await CreateEdgeDeviceAsync(client);

        // At-least-once, as the edge produces it: a batch the broker took but whose acknowledgement
        // the edge never saw is sent again, identical. Then a different batch, so waiting for it
        // proves both copies before it were processed — messages on one topic arrive in order.
        var start = Now().AddMinutes(-5);
        var first = Readings(edge.TagId, start, 10);
        var second = Readings(edge.TagId, start.AddMinutes(1), 10);

        var again = SamplePayload.Write(first, [], DateTimeOffset.UtcNow);
        await PublishAsync(edge.Topic, again);
        await PublishAsync(edge.Topic, again);
        await PublishAsync(edge.Topic, SamplePayload.Write(second, [], DateTimeOffset.UtcNow));

        var history = await WaitForHistoryAsync(client, edge.TagId, samples => samples.Any(s => s.SourceTimestampUtc == second[^1].SourceTimestampUtc));

        // One set: every sample once, at its own time — not two sets, not one merged into the other.
        var measured = first.Count + second.Count;
        Assert.True(
            history.Count == measured,
            $"{measured} samples measured, {history.Count} stored: the repeated batch was stored again.");
        Assert.Equal(
            first.Concat(second).Select(r => r.SourceTimestampUtc).ToList(),
            history.Select(s => s.SourceTimestampUtc).ToList());
        Assert.Equal(
            first.Concat(second).Select(r => ((TagValue.Numeric)r.Value!).Value).ToList(),
            history.Select(s => s.Value.Numeric!.Value).ToList());
    }

    [RequiresDatabaseFact]
    public async Task A_window_the_edge_dropped_reaches_the_journal_as_a_stated_gap()
    {
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());
        var edge = await CreateEdgeDeviceAsync(client);

        // A long outage, compressed: 80 samples, one a second, measured an hour ago, into a buffer
        // that holds 50. They arrive a scan at a time, as acquisition appends them, so the oldest 30
        // are dropped one by one — and recorded as the one window they are.
        var measuredFrom = Now().AddHours(-1);
        var readings = Readings(edge.TagId, measuredFrom, 80);

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 50);
        foreach (var scan in readings)
        {
            buffer.Append([scan]);
        }

        var dropped = buffer.LostWindows();
        Assert.Equal(30, Assert.Single(dropped).Count);

        // The link comes back.
        using (var uplink = Uplink(buffer, edge.EdgeId))
        {
            await uplink.StartAsync(CancellationToken.None);
            await GatewayTestHost.WaitUntilAsync(
                () => Task.FromResult(buffer.Account().Pending == 0 && buffer.UnsentLosses().Count == 0),
                "the edge buffer to drain, losses included");
            await uplink.StopAsync(CancellationToken.None);
        }

        var history = await WaitForHistoryAsync(client, edge.TagId, samples => samples.Count >= 50);

        // What survived arrived with the times the edge recorded; nothing stands in for the rest.
        Assert.Equal(readings.Skip(30).Select(r => r.SourceTimestampUtc).ToList(), history.Select(s => s.SourceTimestampUtc).ToList());
        Assert.DoesNotContain(history, s => s.SourceTimestampUtc <= readings[29].SourceTimestampUtc);

        // And the hole is stated: each window the edge dropped is in the journal, against this
        // device and its Site, saying how many and when.
        var losses = (await JournalAsync(client))
            .Where(entry => entry.Type == "SamplesLost" && entry.DeviceId == edge.DeviceId)
            .OrderBy(entry => entry.GapFromUtc)
            .ToList();

        Assert.True(
            losses.Count == dropped.Count,
            $"The edge dropped {dropped.Count} window(s) ({dropped.Sum(w => w.Count)} samples) but the journal states {losses.Count}: " +
            "the gap in history is unexplained.");
        Assert.All(losses.Zip(dropped), pair =>
        {
            var (entry, window) = pair;
            Assert.Equal(window.Count, entry.LostSamples);
            Assert.Equal(window.FromSourceUtc, entry.GapFromUtc);
            Assert.Equal(window.ToSourceUtc, entry.GapUntilUtc);
            Assert.Equal(DemoConfigurationSeeder.SiteId, entry.SiteId);
        });
        Assert.Equal(readings[0].SourceTimestampUtc, losses[0].GapFromUtc);
        Assert.Equal(readings[29].SourceTimestampUtc, losses[^1].GapUntilUtc);

        // A report that arrives again — its acknowledgement lost, say — is still one entry. The
        // marker sample in the same message shows the Gateway has read it.
        var marker = Readings(edge.TagId, Now().AddMinutes(-1), 1);
        var repeat = SamplePayload.Write(
            marker,
            dropped.Select(w => new SourceLoss(w.LossId, w.Count, w.FromSourceUtc, w.ToSourceUtc)),
            DateTimeOffset.UtcNow);
        await PublishAsync(edge.Topic, repeat);
        await PublishAsync(edge.Topic, repeat);
        await WaitForHistoryAsync(client, edge.TagId, samples => samples.Any(s => s.SourceTimestampUtc == marker[0].SourceTimestampUtc));

        Assert.Equal(
            dropped.Count,
            (await JournalAsync(client)).Count(entry => entry.Type == "SamplesLost" && entry.DeviceId == edge.DeviceId));
    }

    [RequiresDatabaseTheory]
    [InlineData(+10)]
    [InlineData(-10)]
    public async Task An_edge_clock_that_is_off_is_journalled_and_its_times_are_kept(int minutes)
    {
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());
        var edge = await CreateEdgeDeviceAsync(client);

        // The edge's clock is off by ten minutes, and everything it does uses it: the times it
        // measures at and the time it says it sent.
        var edgeClock = new OffsetClock(TimeSpan.FromMinutes(minutes));
        var readings = Readings(edge.TagId, Truncate(edgeClock.GetUtcNow()).AddSeconds(-5), 5);

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 1_000);
        buffer.Append(readings);

        var before = DateTimeOffset.UtcNow;
        using (var uplink = Uplink(buffer, edge.EdgeId, edgeClock))
        {
            await uplink.StartAsync(CancellationToken.None);
            await GatewayTestHost.WaitUntilAsync(() => Task.FromResult(buffer.Account().Pending == 0), "the edge buffer to drain");
            await uplink.StopAsync(CancellationToken.None);
        }

        var history = await WaitForHistoryAsync(client, edge.TagId, samples => samples.Count >= readings.Count);

        // Stored at the times the edge gave, ten minutes off and all: not moved to arrival, not
        // shifted by the measured difference. Inventing better times is not an option (ADR-0017).
        Assert.True(
            history.Count > 0 && history[0].SourceTimestampUtc == readings[0].SourceTimestampUtc,
            $"The edge measured at {readings[0].SourceTimestampUtc:O}; the history holds {history[0].SourceTimestampUtc:O} — " +
            $"moved by {(history[0].SourceTimestampUtc - readings[0].SourceTimestampUtc).TotalSeconds:F0} s.");
        Assert.Equal(readings.Select(r => r.SourceTimestampUtc).ToList(), history.Select(s => s.SourceTimestampUtc).ToList());

        // The difference is written down instead, once, against the device.
        var skew = Assert.Single(
            await JournalAsync(client),
            entry => entry.Type == "SourceClockSkew" && entry.DeviceId == edge.DeviceId);
        Assert.InRange(skew.ClockSkewSeconds!.Value, minutes * 60 - 30, minutes * 60 + 30);
        Assert.Equal(DemoConfigurationSeeder.SiteId, skew.SiteId);

        // Both clocks, neither corrected: the edge's reading as its source time, the Gateway's as
        // the time it was recorded.
        Assert.InRange(skew.RecordedAtUtc, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.InRange(skew.SourceTimeUtc!.Value, before.AddMinutes(minutes).AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(minutes).AddSeconds(30));
    }

    [RequiresDatabaseFact]
    public async Task An_edge_clock_within_tolerance_is_not_journalled()
    {
        // The control for the test above: a clock a few seconds off — transit, an ordinary
        // drift — writes nothing. A rule that journalled every message would pass the test above.
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());
        var edge = await CreateEdgeDeviceAsync(client);

        var edgeClock = new OffsetClock(TimeSpan.FromSeconds(5));
        var readings = Readings(edge.TagId, Truncate(edgeClock.GetUtcNow()).AddSeconds(-5), 5);

        using var buffer = SampleBuffer.Open(_bufferPath, maxPending: 1_000);
        buffer.Append(readings);

        using (var uplink = Uplink(buffer, edge.EdgeId, edgeClock))
        {
            await uplink.StartAsync(CancellationToken.None);
            await GatewayTestHost.WaitUntilAsync(() => Task.FromResult(buffer.Account().Pending == 0), "the edge buffer to drain");
            await uplink.StopAsync(CancellationToken.None);
        }

        await WaitForHistoryAsync(client, edge.TagId, samples => samples.Count >= readings.Count);

        Assert.DoesNotContain(
            await JournalAsync(client),
            entry => entry.Type == "SourceClockSkew" && entry.DeviceId == edge.DeviceId);
    }

    /// <summary>
    /// An MQTT device for one edge, with a data tag and a probe tag, and the Gateway listening:
    /// a message published before it has subscribed would be taken by the broker and delivered
    /// to nobody.
    /// </summary>
    private async Task<EdgeDevice> CreateEdgeDeviceAsync(HttpClient client)
    {
        var edgeId = $"edge-{Guid.NewGuid():N}"[..17];
        var topic = $"scada/edge/{edgeId}/samples";

        using var created = await client.PostAsJsonAsync(
            $"/api/sites/{DemoConfigurationSeeder.SiteId}/devices",
            new SaveDeviceRequest(
                $"Edge {Guid.NewGuid():N}"[..13],
                "mqtt",
                new Dictionary<string, string>
                {
                    ["host"] = "127.0.0.1",
                    ["port"] = _port.ToString(CultureInfo.InvariantCulture),
                    ["topic"] = topic,
                },
                ScanIntervalMs: null,
                FolderId: null));
        created.EnsureSuccessStatusCode();
        var deviceId = await created.Content.ReadFromJsonAsync<Guid>();

        var tagId = await CreateTagAsync(client, deviceId, "Pressure");
        var probeId = await CreateTagAsync(client, deviceId, "Probe");

        // One fixed probe sample, published until it shows: the same sample every time, so the
        // repeats are harmless (they are themselves duplicates, stored once).
        var probe = SamplePayload.Write(Readings(probeId, Now().AddMinutes(-30), 1), [], DateTimeOffset.UtcNow);
        await GatewayTestHost.WaitUntilAsync(
            async () =>
            {
                await PublishAsync(topic, probe);
                using var response = await client.GetAsync($"/api/tags/{probeId}");
                return response.IsSuccessStatusCode
                       && (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quality").GetString() == "Good";
            },
            "the Gateway to subscribe to the new device's topic");

        return new EdgeDevice(deviceId, tagId, edgeId, topic);
    }

    private static async Task<Guid> CreateTagAsync(HttpClient client, Guid deviceId, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/tags",
            new SaveTagRequest(name, "Numeric", Unit: null, name.ToLowerInvariant(), IsWritable: false));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private UplinkService Uplink(SampleBuffer buffer, string edgeId, TimeProvider? clock = null) => new(
        Options.Create(new EdgeOptions
        {
            Id = edgeId,
            Broker = new BrokerOptions { Host = "127.0.0.1", Port = _port },
            Buffer = new BufferOptions { Path = _bufferPath },
        }),
        buffer,
        // This test is about what the cloud does with what arrives; what the edge reads is
        // ConfigurationLinkTests' subject (ADR-0019).
        new EdgeConfigurationConsumer(new EdgeConfigurationSource(), buffer, NullLogger<EdgeConfigurationConsumer>.Instance),
        NullLogger<UplinkService>.Instance,
        clock);

    private async Task PublishAsync(string topic, string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).Build());
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await publisher.DisconnectAsync();
    }

    private static async Task<List<HistorySampleDto>> WaitForHistoryAsync(
        HttpClient client,
        Guid tagId,
        Func<List<HistorySampleDto>, bool> done)
    {
        List<HistorySampleDto> samples = [];
        await GatewayTestHost.WaitUntilAsync(
            async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var from = Uri.EscapeDataString((now - Wide).ToString("O", CultureInfo.InvariantCulture));
                var to = Uri.EscapeDataString((now + Wide).ToString("O", CultureInfo.InvariantCulture));
                var history = await client.GetFromJsonAsync<TagHistoryDto>($"/api/tags/{tagId}/history?from={from}&to={to}");
                samples = history!.Samples.ToList();
                return done(samples);
            },
            $"history of tag {tagId}");

        return samples;
    }

    private static async Task<List<AlarmEventDto>> JournalAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<AlarmEventDto>>("/api/alarms/journal?limit=1000"))!;

    /// <summary>Numeric readings one second apart, each with its own value.</summary>
    private static List<TagReading> Readings(Guid tagId, DateTimeOffset from, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TagReading(tagId, new TagValue.Numeric(i + 0.5), from.AddSeconds(i), Quality.Good))
            .ToList();

    /// <summary>Now, to the millisecond: the database keeps microseconds, the wire keeps ticks.</summary>
    private static DateTimeOffset Now() => Truncate(DateTimeOffset.UtcNow);

    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        new(time.UtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed record EdgeDevice(Guid DeviceId, Guid TagId, string EdgeId, string Topic);

    /// <summary>An edge whose clock is off by a fixed amount.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
