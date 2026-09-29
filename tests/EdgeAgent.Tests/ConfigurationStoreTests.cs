using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// ADR-0019 §5 on the edge's own store: the configuration it accepted is kept across a restart, and
/// the edge comes back reading it — which is why a link outage does not stop a plant being read, and
/// why a configuration the cloud sent once is not needed twice.
/// </summary>
public sealed class ConfigurationStoreTests : IDisposable
{
    private static readonly Guid Pressure = new("99999999-9999-4999-8999-999999999901");
    private static readonly Guid Flow = new("99999999-9999-4999-8999-999999999902");
    private static readonly DateTimeOffset Derived = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = new(2026, 9, 28, 19, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void An_edge_that_has_accepted_nothing_holds_nothing()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 100);

        Assert.Null(buffer.AcceptedConfiguration());

        // And it starts reading nothing, rather than starting on a guess.
        var source = EdgeConfigurationSource.From(buffer, NullLogger.Instance);
        Assert.Null(source.Revision);
        Assert.Empty(source.Devices);
    }

    [Fact]
    public void The_accepted_configuration_survives_a_restart_and_is_what_the_edge_starts_on()
    {
        var message = EdgeConfigurationPayload.Write(Devices(Pressure), Derived);
        var revision = EdgeConfigurationPayload.RevisionOf(Devices(Pressure));

        using (var buffer = SampleBuffer.Open(_path, maxPending: 100))
        {
            var source = new EdgeConfigurationSource();
            var consumer = new EdgeConfigurationConsumer(source, buffer, NullLogger<EdgeConfigurationConsumer>.Instance);

            Assert.True(consumer.Accept(message));
            Assert.Equal(revision, source.Revision);

            var accepted = buffer.AcceptedConfiguration();
            Assert.NotNull(accepted);
            Assert.Equal(revision, accepted.Revision);
            Assert.Equal(Derived, accepted.GeneratedAtUtc);
            Assert.Equal(message, accepted.Payload);
        }

        // Reopened, as the next start of the agent would — with the link down, since nothing here
        // talks to a broker.
        using var reopened = SampleBuffer.Open(_path, maxPending: 100);
        var restarted = EdgeConfigurationSource.From(reopened, NullLogger.Instance);

        Assert.Equal(revision, restarted.Revision);
        var device = Assert.Single(restarted.Devices);
        Assert.Equal("Pump skid", device.Name);
        Assert.Equal("opc-ua", device.Driver);
        Assert.Equal(1000, device.ScanIntervalMs);
        Assert.Equal("opc.tcp://192.0.2.10:4840/Server", device.Settings["endpointUrl"]);

        // The cloud's own tag ids, which is the point of the message (ADR-0019 §7).
        Assert.Equal([Pressure], device.Tags.Select(tag => tag.TagId));
    }

    [Fact]
    public void A_newer_configuration_replaces_the_one_in_the_store()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 100);
        var source = new EdgeConfigurationSource();
        var consumer = new EdgeConfigurationConsumer(source, buffer, NullLogger<EdgeConfigurationConsumer>.Instance);

        Assert.True(consumer.Accept(EdgeConfigurationPayload.Write(Devices(Pressure), Derived)));
        Assert.True(consumer.Accept(EdgeConfigurationPayload.Write(Devices(Pressure, Flow), Derived.AddMinutes(1))));

        var accepted = buffer.AcceptedConfiguration();
        Assert.NotNull(accepted);
        Assert.Equal(EdgeConfigurationPayload.RevisionOf(Devices(Pressure, Flow)), accepted.Revision);
        Assert.Equal([Pressure, Flow], source.Devices.SelectMany(device => device.Tags).Select(tag => tag.TagId));
    }

    [Fact]
    public void The_configuration_is_recorded_when_it_was_received_not_when_the_cloud_derived_it()
    {
        var clock = new FixedClock(Received);

        using var buffer = SampleBuffer.Open(_path, maxPending: 100, clock);
        buffer.AcceptConfiguration("sha256:0", Derived, "{}");

        var accepted = buffer.AcceptedConfiguration();
        Assert.NotNull(accepted);
        Assert.Equal(Received, accepted.ReceivedAtUtc);
        Assert.Equal(Derived, accepted.GeneratedAtUtc);
    }

    [Fact]
    public void A_buffer_written_before_the_configuration_was_kept_upgrades_and_keeps_its_samples()
    {
        // A file of the earlier schema: samples, lost windows and the account, with no configuration
        // table and user_version 2 — the shape the step-4 agent wrote.
        using (var buffer = SampleBuffer.Open(_path, maxPending: 100))
        {
            buffer.Append([new TagReading(Pressure, new TagValue.Numeric(4.75), Derived, Quality.Good)]);
        }

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            db.Open();
            using var downgrade = db.CreateCommand();
            downgrade.CommandText = "DROP TABLE configuration; PRAGMA user_version = 2;";
            downgrade.ExecuteNonQuery();
        }

        using var reopened = SampleBuffer.Open(_path, maxPending: 100);

        // Its samples are still there, and the new table exists and is usable.
        Assert.Single(reopened.Peek(100));
        Assert.Null(reopened.AcceptedConfiguration());

        reopened.AcceptConfiguration("sha256:1", Derived, "{}");
        Assert.Equal("sha256:1", reopened.AcceptedConfiguration()!.Revision);
    }

    [Fact]
    public void A_buffer_written_by_a_newer_agent_is_still_refused()
    {
        using (SampleBuffer.Open(_path, maxPending: 100))
        {
        }

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            db.Open();
            using var upgrade = db.CreateCommand();
            upgrade.CommandText = "PRAGMA user_version = 4;";
            upgrade.ExecuteNonQuery();
        }

        var refusal = Assert.Throws<InvalidOperationException>(() => SampleBuffer.Open(_path, maxPending: 100));
        Assert.Contains("newer edge agent", refusal.Message, StringComparison.Ordinal);
    }

    private static List<EdgeConfigurationDevice> Devices(params Guid[] tagIds) =>
    [
        new(
            "Pump skid",
            "opc-ua",
            1000,
            new Dictionary<string, string> { ["endpointUrl"] = "opc.tcp://192.0.2.10:4840/Server" },
            tagIds.Select(id => new EdgeConfigurationTag(id, $"ns=2;s=Pump1.{id:N}", TagValueKind.Numeric)).ToList()),
    ];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
