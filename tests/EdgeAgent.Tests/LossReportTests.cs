using Microsoft.Data.Sqlite;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Uplink;

namespace ScadaDarbox.EdgeAgent.Tests;

/// <summary>
/// The edge's half of reporting a loss (ADR-0017): every lost window has an id, is reported until
/// an acknowledged message has carried it, and a buffer from the step-3 agent gains both without
/// losing what it recorded.
/// </summary>
public sealed class LossReportTests : IDisposable
{
    private static readonly Guid Tag = new("66666666-6666-4666-8666-666666666611");
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edge-buffer-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_loss_is_marked_sent_only_by_the_acknowledgement_of_the_message_that_carried_it()
    {
        using (var buffer = SampleBuffer.Open(_path, maxPending: 10))
        {
            buffer.Append(Samples(0, 15));
            var loss = Assert.Single(buffer.UnsentLosses());
            Assert.NotEqual(Guid.Empty, loss.LossId);

            // A message that did not carry it: acknowledged, but the loss is still to be reported.
            buffer.Acknowledge(buffer.Peek(3)[^1].Sequence, []);
            Assert.Single(buffer.UnsentLosses());

            // The one that did.
            buffer.Acknowledge(null, [loss.LossId]);
            Assert.Empty(buffer.UnsentLosses());

            // Reported, and still counted: the accounting needs every window, sent or not.
            Assert.True(Assert.Single(buffer.LostWindows()).Sent);
            Assert.Empty(buffer.Account().Problems());
        }

        // And that survives a restart: a loss already reported is not reported again.
        using var reopened = SampleBuffer.Open(_path, maxPending: 10);
        Assert.Empty(reopened.UnsentLosses());
    }

    [Fact]
    public void A_long_outage_is_one_window_not_one_per_scan()
    {
        // Acquisition appends a scan at a time, so a full buffer drops a sample with every append.
        // Found by running the real agent: a 45-second cut became 130 journal entries.
        using var buffer = SampleBuffer.Open(_path, maxPending: 10);

        foreach (var scan in Samples(0, 100))
        {
            buffer.Append([scan]);
        }

        var window = Assert.Single(buffer.LostWindows());
        Assert.Equal((1L, 90L, 90L), (window.FromSequence, window.ToSequence, window.Count));
        Assert.Equal((Start, Start.AddSeconds(89)), (window.FromSourceUtc, window.ToSourceUtc));
        Assert.Empty(buffer.Account().Problems());
    }

    [Fact]
    public void A_window_taken_into_a_message_is_never_changed_and_later_drops_start_a_new_one()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 10);
        foreach (var scan in Samples(0, 15))
        {
            buffer.Append([scan]);
        }

        // The uplink puts the window in a message. The cloud may record it from that message.
        var sent = Assert.Single(buffer.TakeLossesToSend());
        Assert.Equal(5L, sent.Count);

        // Acquisition goes on dropping before the acknowledgement comes back.
        foreach (var scan in Samples(15, 3))
        {
            buffer.Append([scan]);
        }

        // What the message said stays true: that window is still five. Grown to eight under the
        // same id, the cloud — which already has the id — would never hear of the other three.
        var windows = buffer.LostWindows();
        Assert.Equal(2, windows.Count);
        Assert.Equal((sent.LossId, 5L), (windows[0].LossId, windows[0].Count));
        Assert.Equal((6L, 8L, 3L, false), (windows[1].FromSequence, windows[1].ToSequence, windows[1].Count, windows[1].Sealed));

        buffer.Acknowledge(null, [sent.LossId]);
        Assert.Equal(windows[1].LossId, Assert.Single(buffer.UnsentLosses()).LossId);
        Assert.Empty(buffer.Account().Problems());
    }

    [Fact]
    public void A_buffer_from_the_step_3_agent_keeps_its_samples_and_its_losses_become_reportable()
    {
        // Written exactly as the schema-1 agent wrote it: five samples dropped and recorded,
        // ten waiting, no ids and no sent flag.
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var create = db.CreateCommand();
            create.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE sample (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT, tag_id TEXT NOT NULL, source_ticks INTEGER NOT NULL,
                    quality INTEGER NOT NULL, kind INTEGER, numeric REAL, boolean INTEGER, text TEXT, code INTEGER, label TEXT);
                CREATE TABLE lost_window (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, from_seq INTEGER NOT NULL, to_seq INTEGER NOT NULL,
                    sample_count INTEGER NOT NULL, from_source_ticks INTEGER NOT NULL, to_source_ticks INTEGER NOT NULL,
                    recorded_ticks INTEGER NOT NULL);
                CREATE TABLE account (
                    id INTEGER PRIMARY KEY CHECK (id = 1), appended INTEGER NOT NULL, acknowledged INTEGER NOT NULL,
                    lost INTEGER NOT NULL, cursor INTEGER NOT NULL);
                INSERT INTO account VALUES (1, 15, 0, 5, 5);
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();

            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO sample (seq, tag_id, source_ticks, quality, kind, numeric) VALUES ($seq, $tag, $ticks, 0, 0, $value)";
            var seq = insert.Parameters.Add("$seq", SqliteType.Integer);
            insert.Parameters.AddWithValue("$tag", Tag.ToString());
            var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
            var value = insert.Parameters.Add("$value", SqliteType.Real);
            for (var i = 6; i <= 15; i++)
            {
                seq.Value = i;
                ticks.Value = Start.AddSeconds(i - 1).UtcTicks;
                value.Value = (double)(i - 1);
                insert.ExecuteNonQuery();
            }

            using var window = db.CreateCommand();
            window.CommandText = $"INSERT INTO lost_window (from_seq, to_seq, sample_count, from_source_ticks, to_source_ticks, recorded_ticks) " +
                                 $"VALUES (1, 5, 5, {Start.UtcTicks}, {Start.AddSeconds(4).UtcTicks}, {Start.AddSeconds(15).UtcTicks})";
            window.ExecuteNonQuery();
        }

        using var buffer = SampleBuffer.Open(_path, maxPending: 10);

        // Nothing it held is lost to the upgrade.
        var account = buffer.Account();
        Assert.Empty(account.Problems());
        Assert.Equal((Appended: 15L, Pending: 10L, Lost: 5L), (account.Appended, account.Pending, account.Lost));
        Assert.Equal(Samples(5, 10), buffer.Peek(100).Select(sample => sample.Reading));

        // The step-3 agent never sent a loss, so the one it recorded now waits to be reported,
        // with an id of its own and its window unchanged.
        var loss = Assert.Single(buffer.UnsentLosses());
        Assert.NotEqual(Guid.Empty, loss.LossId);
        Assert.Equal((5L, Start, Start.AddSeconds(4)), (loss.Count, loss.FromSourceUtc, loss.ToSourceUtc));
    }

    /// <summary>
    /// Not a guarantee but a measurement: the one way the edge reports as lost samples the cloud
    /// actually has, and how large it can be.
    /// </summary>
    [Fact]
    public void A_batch_dropped_while_in_flight_is_reported_lost_though_the_broker_took_it()
    {
        using var buffer = SampleBuffer.Open(_path, maxPending: 1_000);
        buffer.Append(Samples(0, 1_000));

        // 1. The uplink takes the oldest batch and publishes it.
        var inFlight = buffer.Peek(UplinkService.BatchSize);
        Assert.Equal((1L, 500L), (inFlight[0].Sequence, inFlight[^1].Sequence));

        // 2. Before the acknowledgement arrives, acquisition appends 300 more to a full buffer. The
        //    oldest 300 go — and the oldest are the batch in flight.
        buffer.Append(Samples(1_000, 300));
        var window = Assert.Single(buffer.UnsentLosses());
        Assert.Equal((1L, 300L, 300L), (window.FromSequence, window.ToSequence, window.Count));

        // 3. The broker acknowledges the batch: it has all 500. Only the 200 still here can be
        //    counted as acknowledged; the other 300 stay counted lost.
        buffer.Acknowledge(inFlight[^1].Sequence, []);

        var account = buffer.Account();
        Assert.Empty(account.Problems());
        Assert.Equal((Acknowledged: 200L, Lost: 300L), (account.Acknowledged, account.Lost));

        // So the cloud holds 300 samples inside a window the edge will report as lost. The overlap
        // is the part of the in-flight batch the drop took: never more than one batch, because one
        // publish at a time is in flight, and it is always the oldest end of the window.
        var overlap = inFlight.Count(sample => sample.Sequence >= window.FromSequence && sample.Sequence <= window.ToSequence);
        Assert.Equal(300, overlap);
        Assert.True(overlap <= UplinkService.BatchSize);

        // Once acknowledged, nothing further back is in flight: the next batch starts after it.
        Assert.Equal(501L, buffer.Peek(1)[0].Sequence);
    }

    private static List<TagReading> Samples(int from, int count) =>
        Enumerable.Range(from, count)
            .Select(i => new TagReading(Tag, new TagValue.Numeric(i), Start.AddSeconds(i), Quality.Good))
            .ToList();
}
