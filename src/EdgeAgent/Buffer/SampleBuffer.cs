using System.Globalization;
using Microsoft.Data.Sqlite;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.EdgeAgent.Buffer;

/// <summary>
/// The edge's send buffer (ADR-0017, ADR-0018): samples wait here, on disk, until the broker has
/// acknowledged them. It survives a restart and a killed process, it is bounded, and when a long
/// outage fills it the oldest samples are dropped — never silently.
/// </summary>
/// <remarks>
/// <para>
/// The accounting it keeps, and which the tests hold it to:
/// <c>appended = pending + acknowledged + lost</c>, the recorded lost windows add up to
/// <c>lost</c>, and everything at or below the cursor is settled — acknowledged by the broker, or
/// dropped with its window recorded — while everything above it is still in the table.
/// </para>
/// <para>
/// Dropping is three steps: delete the oldest samples, record the window they covered, move the
/// cursor. They are one transaction, and the same transaction as the append that caused them, so
/// a crash anywhere in between leaves the buffer as it was before the append. Split, a crash
/// between them leaves samples gone that nothing accounts for.
/// </para>
/// <para>
/// A lost window is reported to the cloud until a message carrying it is acknowledged, then kept
/// here marked sent: the accounting above still needs it. Each has an id of its own, so the cloud
/// records a report it receives twice once (ADR-0017).
/// </para>
/// <para>
/// One outage is one window, not one per scan. Acquisition appends a scan at a time, so a full
/// buffer drops a sample or two with every append; recorded separately, nine hours of outage would
/// be tens of thousands of journal entries saying the same thing. A drop therefore extends the last
/// window when it continues it — but only while that window is unsealed: once the uplink has taken a
/// window into a message it is sealed, because the cloud may already hold it under its id, and a
/// window changed after that would never be recorded at its new size.
/// </para>
/// <para>
/// SQLite in WAL mode with <c>synchronous=FULL</c>: a committed transaction survives a power cut
/// (SQLite's guarantee, not one we can test — ADR-0018); an uncommitted one never appears.
/// </para>
/// </remarks>
public sealed class SampleBuffer : IDisposable
{
    // 1: samples, lost windows, the account. 2: each lost window has an id, and sealed and sent flags.
    private const int SchemaVersion = 2;

    private readonly SqliteConnection _db;
    private readonly long _maxPending;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private SampleBuffer(SqliteConnection db, long maxPending, TimeProvider clock)
    {
        _db = db;
        _maxPending = maxPending;
        _clock = clock;
    }

    /// <summary>For tests only: called between the steps of a drop — "deleted", then "cursor moved".</summary>
    internal Action<string>? BetweenDropSteps { get; set; }

    /// <summary>For tests only: called after each row of an append is written, before the append commits.</summary>
    internal Action<int>? AfterRowWritten { get; set; }

    /// <summary>Opens the buffer at <paramref name="path"/>, creating it if it does not exist.</summary>
    /// <param name="maxPending">How many samples may wait before the oldest are dropped.</param>
    public static SampleBuffer Open(string path, long maxPending, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Unpooled: the file is this process's, and a pooled connection kept open after Dispose
        // would keep the file locked for the next Open in the same process.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();

        try
        {
            Execute(db, null, "PRAGMA journal_mode=WAL;");
            Execute(db, null, "PRAGMA synchronous=FULL;");

            var version = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The buffer at '{path}' was written by a newer edge agent (schema {version}; this build knows {SchemaVersion}). " +
                    "Its samples are kept; run the newer agent, or move the file aside.");
            }

            using (var create = db.BeginTransaction())
            {
                if (version == 1)
                {
                    UpgradeFromVersion1(db, create);
                }

                Execute(db, create, """
                    CREATE TABLE IF NOT EXISTS sample (
                        seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                        tag_id       TEXT    NOT NULL,
                        source_ticks INTEGER NOT NULL,
                        quality      INTEGER NOT NULL,
                        kind         INTEGER,
                        numeric      REAL,
                        boolean      INTEGER,
                        text         TEXT,
                        code         INTEGER,
                        label        TEXT
                    );
                    CREATE TABLE IF NOT EXISTS lost_window (
                        id                INTEGER PRIMARY KEY AUTOINCREMENT,
                        from_seq          INTEGER NOT NULL,
                        to_seq            INTEGER NOT NULL,
                        sample_count      INTEGER NOT NULL,
                        from_source_ticks INTEGER NOT NULL,
                        to_source_ticks   INTEGER NOT NULL,
                        recorded_ticks    INTEGER NOT NULL,
                        loss_id           TEXT    NOT NULL,
                        sealed            INTEGER NOT NULL DEFAULT 0,
                        sent              INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS account (
                        id           INTEGER PRIMARY KEY CHECK (id = 1),
                        appended     INTEGER NOT NULL,
                        acknowledged INTEGER NOT NULL,
                        lost         INTEGER NOT NULL,
                        cursor       INTEGER NOT NULL
                    );
                    INSERT OR IGNORE INTO account (id, appended, acknowledged, lost, cursor) VALUES (1, 0, 0, 0, 0);
                    PRAGMA user_version = 2;
                    """);
                create.Commit();
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return new SampleBuffer(db, maxPending, clock ?? TimeProvider.System);
    }

    /// <summary>
    /// Adds one batch — all of it or none of it — and, if that takes the buffer past its bound,
    /// drops the oldest samples in the same transaction, recording what was lost.
    /// </summary>
    public void Append(IReadOnlyList<TagReading> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();

            using (var insert = _db.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sample (tag_id, source_ticks, quality, kind, numeric, boolean, text, code, label)
                    VALUES ($tag, $ticks, $quality, $kind, $numeric, $boolean, $text, $code, $label)
                    """;
                var tag = insert.Parameters.Add("$tag", SqliteType.Text);
                var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
                var quality = insert.Parameters.Add("$quality", SqliteType.Integer);
                var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
                var numeric = insert.Parameters.Add("$numeric", SqliteType.Real);
                var boolean = insert.Parameters.Add("$boolean", SqliteType.Integer);
                var text = insert.Parameters.Add("$text", SqliteType.Text);
                var code = insert.Parameters.Add("$code", SqliteType.Integer);
                var label = insert.Parameters.Add("$label", SqliteType.Text);

                for (var index = 0; index < batch.Count; index++)
                {
                    var reading = batch[index];
                    tag.Value = reading.TagId.ToString();
                    ticks.Value = reading.SourceTimestampUtc.UtcTicks;
                    quality.Value = (int)reading.Quality;
                    kind.Value = reading.Value is null ? DBNull.Value : (int)reading.Value.Kind;
                    numeric.Value = reading.Value is TagValue.Numeric n ? n.Value : DBNull.Value;
                    boolean.Value = reading.Value is TagValue.Boolean b ? (b.Value ? 1 : 0) : DBNull.Value;
                    text.Value = reading.Value is TagValue.Text t ? t.Value : DBNull.Value;
                    code.Value = reading.Value is TagValue.Discrete d ? d.Code : DBNull.Value;
                    label.Value = reading.Value is TagValue.Discrete { Label: { } l } ? l : DBNull.Value;
                    insert.ExecuteNonQuery();

                    AfterRowWritten?.Invoke(index);
                }
            }

            Execute(_db, transaction, $"UPDATE account SET appended = appended + {batch.Count} WHERE id = 1;");

            DropOverflow(transaction);

            transaction.Commit();
        }
    }

    /// <summary>The oldest waiting samples, in the order they were appended, for sending.</summary>
    public IReadOnlyList<BufferedSample> Peek(int max)
    {
        lock (_gate)
        {
            using var read = _db.CreateCommand();
            read.CommandText = """
                SELECT seq, tag_id, source_ticks, quality, kind, numeric, boolean, text, code, label
                FROM sample ORDER BY seq LIMIT $max
                """;
            read.Parameters.AddWithValue("$max", max);

            var samples = new List<BufferedSample>();
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                samples.Add(new BufferedSample(reader.GetInt64(0), ReadSample(reader)));
            }

            return samples;
        }
    }

    /// <summary>Lost windows not yet carried by an acknowledged message, oldest first.</summary>
    public IReadOnlyList<LostWindow> UnsentLosses() => ReadLostWindows("WHERE sent = 0");

    /// <summary>
    /// The lost windows to put in the next message: every one not yet acknowledged, each sealed so
    /// that no later drop changes what that message said about it. A drop after this starts a new
    /// window.
    /// </summary>
    public IReadOnlyList<LostWindow> TakeLossesToSend()
    {
        lock (_gate)
        {
            Execute(_db, null, "UPDATE lost_window SET sealed = 1 WHERE sent = 0 AND sealed = 0;");
            return ReadLostWindows("WHERE sent = 0");
        }
    }

    /// <summary>
    /// The broker has acknowledged everything up to <paramref name="throughSequence"/>: remove it
    /// and move the cursor, in one transaction (ADR-0017 — removed only once acknowledged).
    /// </summary>
    /// <remarks>
    /// Only samples still here are counted as acknowledged. If a full buffer dropped some of a
    /// batch while it was being sent, those were recorded lost and stay counted as lost: the cloud
    /// may then have samples inside a window the edge reported lost — an overstated gap, which is
    /// the safe direction to be wrong in; the reverse would hide one.
    /// </remarks>
    public void Acknowledge(long throughSequence) => Acknowledge(throughSequence, []);

    /// <summary>
    /// The broker has acknowledged a message: the samples in it up to
    /// <paramref name="throughSequence"/> (none, if null) and the loss reports
    /// <paramref name="lossIds"/>. One transaction, so a report is never marked sent without the
    /// samples that travelled with it, or the reverse.
    /// </summary>
    public void Acknowledge(long? throughSequence, IReadOnlyCollection<Guid> lossIds)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();

            using (var sent = _db.CreateCommand())
            {
                sent.Transaction = transaction;
                sent.CommandText = "UPDATE lost_window SET sent = 1 WHERE loss_id = $loss";
                var loss = sent.Parameters.Add("$loss", SqliteType.Text);
                foreach (var lossId in lossIds)
                {
                    loss.Value = lossId.ToString();
                    sent.ExecuteNonQuery();
                }
            }

            if (throughSequence is null)
            {
                transaction.Commit();
                return;
            }

            using var delete = _db.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sample WHERE seq <= $through";
            delete.Parameters.AddWithValue("$through", throughSequence);
            var removed = delete.ExecuteNonQuery();

            using var account = _db.CreateCommand();
            account.Transaction = transaction;
            account.CommandText = """
                UPDATE account SET acknowledged = acknowledged + $removed, cursor = max(cursor, $through) WHERE id = 1
                """;
            account.Parameters.AddWithValue("$removed", removed);
            account.Parameters.AddWithValue("$through", throughSequence);
            account.ExecuteNonQuery();

            transaction.Commit();
        }
    }

    /// <summary>The buffer's own accounting, as stored.</summary>
    public BufferAccount Account()
    {
        lock (_gate)
        {
            using var read = _db.CreateCommand();
            read.CommandText = """
                SELECT a.appended, (SELECT count(*) FROM sample), a.acknowledged, a.lost, a.cursor,
                       (SELECT coalesce(sum(sample_count), 0) FROM lost_window),
                       (SELECT min(seq) FROM sample)
                FROM account a WHERE a.id = 1
                """;
            using var reader = read.ExecuteReader();
            reader.Read();
            return new BufferAccount(
                Appended: reader.GetInt64(0),
                Pending: reader.GetInt64(1),
                Acknowledged: reader.GetInt64(2),
                Lost: reader.GetInt64(3),
                Cursor: reader.GetInt64(4),
                LostInRecordedWindows: reader.GetInt64(5),
                OldestPendingSequence: reader.IsDBNull(6) ? null : reader.GetInt64(6));
        }
    }

    /// <summary>Every window of samples dropped because the buffer was full.</summary>
    public IReadOnlyList<LostWindow> LostWindows() => ReadLostWindows("");

    private IReadOnlyList<LostWindow> ReadLostWindows(string where)
    {
        lock (_gate)
        {
            using var read = _db.CreateCommand();
            read.CommandText = $"""
                SELECT from_seq, to_seq, sample_count, from_source_ticks, to_source_ticks, recorded_ticks, loss_id, sealed, sent
                FROM lost_window {where} ORDER BY id
                """;
            var windows = new List<LostWindow>();
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                windows.Add(new LostWindow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    Guid.Parse(reader.GetString(6)),
                    Sealed: reader.GetInt64(7) != 0,
                    Sent: reader.GetInt64(8) != 0));
            }

            return windows;
        }
    }

    /// <summary>SQLite's own integrity check: "ok", or what it found.</summary>
    public string IntegrityCheck()
    {
        lock (_gate)
        {
            return Convert.ToString(Scalar(_db, null, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture) ?? "(nothing)";
        }
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Drops the oldest samples beyond the bound, inside the caller's transaction: delete them,
    /// move the cursor past them, record the window they covered.
    /// </summary>
    private void DropOverflow(SqliteTransaction transaction)
    {
        var pending = Convert.ToInt64(Scalar(_db, transaction, "SELECT count(*) FROM sample;"), CultureInfo.InvariantCulture);
        var excess = pending - _maxPending;
        if (excess <= 0)
        {
            return;
        }

        using var span = _db.CreateCommand();
        span.Transaction = transaction;
        span.CommandText = """
            SELECT min(seq), max(seq), count(*), min(source_ticks), max(source_ticks)
            FROM (SELECT seq, source_ticks FROM sample ORDER BY seq LIMIT $excess)
            """;
        span.Parameters.AddWithValue("$excess", excess);

        long fromSeq, toSeq, count, fromTicks, toTicks;
        using (var reader = span.ExecuteReader())
        {
            reader.Read();
            (fromSeq, toSeq, count, fromTicks, toTicks) =
                (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }

        // 1. Delete.
        Execute(_db, transaction, $"DELETE FROM sample WHERE seq <= {toSeq};");
        BetweenDropSteps?.Invoke("deleted");

        // 2. Move the cursor past them, counting them lost.
        Execute(_db, transaction, $"UPDATE account SET lost = lost + {count}, cursor = max(cursor, {toSeq}) WHERE id = 1;");
        BetweenDropSteps?.Invoke("cursor moved");

        // 3. Record what was lost: which samples, and the span of source time they covered —
        //    as more of the last window, if it is still open and this drop continues it.
        using var extend = _db.CreateCommand();
        extend.Transaction = transaction;
        extend.CommandText = """
            UPDATE lost_window
            SET to_seq = $to, sample_count = sample_count + $count,
                from_source_ticks = min(from_source_ticks, $fromTicks),
                to_source_ticks = max(to_source_ticks, $toTicks),
                recorded_ticks = $recorded
            WHERE id = (SELECT max(id) FROM lost_window)
              AND sealed = 0 AND sent = 0 AND to_seq = $from - 1
            """;
        extend.Parameters.AddWithValue("$from", fromSeq);
        extend.Parameters.AddWithValue("$to", toSeq);
        extend.Parameters.AddWithValue("$count", count);
        extend.Parameters.AddWithValue("$fromTicks", fromTicks);
        extend.Parameters.AddWithValue("$toTicks", toTicks);
        extend.Parameters.AddWithValue("$recorded", _clock.GetUtcNow().UtcTicks);
        if (extend.ExecuteNonQuery() == 1)
        {
            return;
        }

        using var record = _db.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO lost_window (from_seq, to_seq, sample_count, from_source_ticks, to_source_ticks, recorded_ticks, loss_id)
            VALUES ($from, $to, $count, $fromTicks, $toTicks, $recorded, $lossId)
            """;
        record.Parameters.AddWithValue("$from", fromSeq);
        record.Parameters.AddWithValue("$to", toSeq);
        record.Parameters.AddWithValue("$count", count);
        record.Parameters.AddWithValue("$fromTicks", fromTicks);
        record.Parameters.AddWithValue("$toTicks", toTicks);
        record.Parameters.AddWithValue("$recorded", _clock.GetUtcNow().UtcTicks);
        record.Parameters.AddWithValue("$lossId", Guid.NewGuid().ToString());
        record.ExecuteNonQuery();
    }

    /// <summary>
    /// A buffer written by the step-3 agent: its lost windows gain an id and a sent flag. None of
    /// them was ever reported — that agent did not send losses — so all start unsent, and the
    /// cloud hears about them on the first acknowledged message.
    /// </summary>
    private static void UpgradeFromVersion1(SqliteConnection db, SqliteTransaction transaction)
    {
        Execute(db, transaction, "ALTER TABLE lost_window ADD COLUMN loss_id TEXT;");
        Execute(db, transaction, "ALTER TABLE lost_window ADD COLUMN sealed INTEGER NOT NULL DEFAULT 0;");
        Execute(db, transaction, "ALTER TABLE lost_window ADD COLUMN sent INTEGER NOT NULL DEFAULT 0;");

        var ids = new List<long>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM lost_window WHERE loss_id IS NULL";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        using var assign = db.CreateCommand();
        assign.Transaction = transaction;
        assign.CommandText = "UPDATE lost_window SET loss_id = $lossId WHERE id = $id";
        var lossId = assign.Parameters.Add("$lossId", SqliteType.Text);
        var id = assign.Parameters.Add("$id", SqliteType.Integer);
        foreach (var row in ids)
        {
            lossId.Value = Guid.NewGuid().ToString();
            id.Value = row;
            assign.ExecuteNonQuery();
        }
    }

    private static TagReading ReadSample(SqliteDataReader reader)
    {
        var tagId = Guid.Parse(reader.GetString(1));
        var measuredAt = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero);
        var quality = (Quality)reader.GetInt32(3);

        TagValue? value = reader.IsDBNull(4)
            ? null
            : (TagValueKind)reader.GetInt32(4) switch
            {
                TagValueKind.Numeric => new TagValue.Numeric(reader.GetDouble(5)),
                TagValueKind.Boolean => new TagValue.Boolean(reader.GetInt64(6) != 0),
                TagValueKind.Text => new TagValue.Text(reader.GetString(7)),
                TagValueKind.Discrete => new TagValue.Discrete(reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9)),
                var other => throw new InvalidOperationException($"The buffer holds a value of unknown kind {other}."),
            };

        return new TagReading(tagId, value, measuredAt, quality);
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, SqliteTransaction? transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

/// <summary>A sample waiting to be sent, with its place in the buffer.</summary>
public sealed record BufferedSample(long Sequence, TagReading Reading);

/// <summary>Samples dropped because the buffer was full: which, and the source time they spanned (ADR-0017).</summary>
/// <param name="LossId">The id the cloud records this loss under, once however often it is sent.</param>
/// <param name="Sealed">Taken into a message: no later drop extends it.</param>
/// <param name="Sent">Whether a message carrying it has been acknowledged.</param>
public sealed record LostWindow(
    long FromSequence,
    long ToSequence,
    long Count,
    DateTimeOffset FromSourceUtc,
    DateTimeOffset ToSourceUtc,
    DateTimeOffset RecordedAtUtc,
    Guid LossId,
    bool Sealed,
    bool Sent);

/// <summary>The buffer's accounting.</summary>
/// <param name="LostInRecordedWindows">The sum of every recorded lost window — must equal <paramref name="Lost"/>.</param>
public sealed record BufferAccount(
    long Appended,
    long Pending,
    long Acknowledged,
    long Lost,
    long Cursor,
    long LostInRecordedWindows,
    long? OldestPendingSequence)
{
    /// <summary>
    /// What is wrong with the accounting, or nothing: every sample ever appended is waiting,
    /// acknowledged, or inside a recorded lost window; nothing waiting is at or below the cursor.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Pending + Acknowledged + Lost != Appended)
        {
            problems.Add(
                $"{Appended - Pending - Acknowledged - Lost} sample(s) appended are neither waiting, acknowledged nor recorded lost");
        }

        if (LostInRecordedWindows != Lost)
        {
            problems.Add(
                $"{Lost} sample(s) counted lost but {LostInRecordedWindows} inside recorded windows: the cursor passed samples that were never sent and never recorded as lost");
        }

        if (OldestPendingSequence is { } oldest && oldest <= Cursor)
        {
            problems.Add($"sample {oldest} is waiting at or below the cursor ({Cursor})");
        }

        return problems;
    }
}
