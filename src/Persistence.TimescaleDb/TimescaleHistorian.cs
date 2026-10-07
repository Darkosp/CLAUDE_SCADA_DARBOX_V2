using Npgsql;
using NpgsqlTypes;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// The concrete historian behind core's <see cref="IHistorian"/> abstraction: a plain
/// TimescaleDB hypertable (ADR-0006). Core never sees this type.
/// </summary>
public sealed class TimescaleHistorian : IHistorian
{
    private readonly NpgsqlDataSource _dataSource;

    public TimescaleHistorian(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task WriteAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        // Both timestamps are written as supplied. A sample whose source time is older
        // than one already stored is appended as-is: out-of-order arrivals land at their
        // true point in history rather than at arrival time (ADR-0003).
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY tag_sample (tag_id, source_time, ingested_at, quality, value_kind,
                             numeric_value, boolean_value, text_value, discrete_code, discrete_label)
            FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken).ConfigureAwait(false);

        foreach (var sample in samples)
        {
            var (numeric, boolean, text, code, label) = TagValueMapping.ToColumns(sample.Value);

            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(sample.TagId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(sample.SourceTimestampUtc, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(sample.IngestedAtUtc, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((short)sample.Quality, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(
                writer, (short?)sample.Value?.Kind, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(writer, numeric, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(writer, boolean, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(writer, text, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(writer, code, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(writer, label, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> WriteOnceAsync(IReadOnlyList<HistorianSample> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        // Not COPY: COPY has no ON CONFLICT. One statement over parallel arrays instead, so a
        // batch is still a single round trip. The conflict target names the partial index from
        // migration 0011 by its columns and predicate; a duplicate inside the same batch is
        // dropped the same way as one already stored.
        var count = samples.Count;
        var tagIds = new Guid[count];
        var sourceTimes = new DateTimeOffset[count];
        var ingestedAt = new DateTimeOffset[count];
        var qualities = new short[count];
        var kinds = new short?[count];
        var numerics = new double?[count];
        var booleans = new bool?[count];
        var texts = new string?[count];
        var codes = new int?[count];
        var labels = new string?[count];

        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            tagIds[index] = sample.TagId;
            sourceTimes[index] = sample.SourceTimestampUtc.ToUniversalTime();
            ingestedAt[index] = sample.IngestedAtUtc.ToUniversalTime();
            qualities[index] = (short)sample.Quality;
            kinds[index] = (short?)sample.Value?.Kind;
            (numerics[index], booleans[index], texts[index], codes[index], labels[index]) = TagValueMapping.ToColumns(sample.Value);
        }

        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO tag_sample (tag_id, source_time, ingested_at, quality, value_kind,
                                    numeric_value, boolean_value, text_value, discrete_code, discrete_label, pushed)
            SELECT tag_id, source_time, ingested_at, quality, value_kind,
                   numeric_value, boolean_value, text_value, discrete_code, discrete_label, true
            FROM unnest(@tag_ids, @source_times, @ingested_at, @qualities, @kinds,
                        @numerics, @booleans, @texts, @codes, @labels)
                 AS s (tag_id, source_time, ingested_at, quality, value_kind,
                       numeric_value, boolean_value, text_value, discrete_code, discrete_label)
            ON CONFLICT (tag_id, source_time) WHERE pushed DO NOTHING
            """);

        command.Parameters.AddWithValue("tag_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, tagIds);
        command.Parameters.AddWithValue("source_times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, sourceTimes);
        command.Parameters.AddWithValue("ingested_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, ingestedAt);
        command.Parameters.AddWithValue("qualities", NpgsqlDbType.Array | NpgsqlDbType.Smallint, qualities);
        command.Parameters.AddWithValue("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint, kinds);
        command.Parameters.AddWithValue("numerics", NpgsqlDbType.Array | NpgsqlDbType.Double, numerics);
        command.Parameters.AddWithValue("booleans", NpgsqlDbType.Array | NpgsqlDbType.Boolean, booleans);
        command.Parameters.AddWithValue("texts", NpgsqlDbType.Array | NpgsqlDbType.Text, texts);
        command.Parameters.AddWithValue("codes", NpgsqlDbType.Array | NpgsqlDbType.Integer, codes);
        command.Parameters.AddWithValue("labels", NpgsqlDbType.Array | NpgsqlDbType.Text, labels);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistorianSample>> ReadAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_time, ingested_at, quality, value_kind,
                   numeric_value, boolean_value, text_value, discrete_code, discrete_label
            FROM tag_sample
            WHERE tag_id = @tag_id AND source_time >= @from AND source_time < @to
            ORDER BY source_time
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tag_id", tagId);
        command.Parameters.AddWithValue("from", fromUtc);
        command.Parameters.AddWithValue("to", toUtc);

        var results = new List<HistorianSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TagValueKind? kind = reader.IsDBNull(3) ? null : (TagValueKind)reader.GetInt16(3);
            var value = TagValueMapping.FromColumns(
                kind,
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));

            results.Add(new HistorianSample(
                tagId,
                value,
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                (Quality)reader.GetInt16(2)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HistorianBucket>> ReadBucketsAsync(
        Guid tagId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketWidth,
        CancellationToken cancellationToken)
    {
        // A bucket of no width is not a bucket, and date_bin refuses one rather than guessing —
        // so the refusal is here, naming what arrived, instead of a Postgres error naming a stride.
        if (bucketWidth <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketWidth),
                bucketWidth,
                "A bucketed read needs a width above zero.");
        }

        // **The reduction is the query** (ADR-0029). The readings that are not drawn are never
        // built, never sent and never materialised by the Gateway — which is the whole point of
        // deciding this here rather than in the browser or in the host.
        //
        // date_bin is PostgreSQL's own function, not a TimescaleDB one, so nothing here leans on
        // the licensed features ADR-0006 flags. Its origin is the window's own start, so the first
        // bucket begins exactly at @from and the same request returns the same grid.
        //
        // `plottable` is written once, as a CASE, because the alternative is the same predicate
        // twice in a min() and a max() — the shape this project has been bitten by. It means what
        // the client used to mean by `usableSamples`: Good and numeric. **The three comparisons
        // against NaN and the infinities are not decoration.** JSON carries neither, so the raw
        // read sends such a reading as "no value" and a trend drops it; an envelope built over
        // them would show a value a raw read of the same window never would. Postgres' float
        // ordering puts NaN above every number and treats it as equal to itself, so `<>` excludes
        // it exactly.
        const string sql = """
            WITH readings AS (
                SELECT source_time,
                       CASE WHEN quality = @good
                             AND numeric_value IS NOT NULL
                             AND numeric_value <> 'NaN'::float8
                             AND numeric_value <> 'Infinity'::float8
                             AND numeric_value <> '-Infinity'::float8
                            THEN numeric_value END AS plottable
                FROM tag_sample
                WHERE tag_id = @tag_id AND source_time >= @from AND source_time < @to
            )
            SELECT date_bin(@width, source_time, @from) AS bucket_start,
                   count(*) AS readings,
                   max(source_time) AS last_reading,
                   min(plottable) AS low_value,
                   max(plottable) AS high_value
            FROM readings
            GROUP BY bucket_start
            ORDER BY bucket_start
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tag_id", tagId);
        command.Parameters.AddWithValue("from", fromUtc);
        command.Parameters.AddWithValue("to", toUtc);
        command.Parameters.AddWithValue("good", (short)Quality.Good);
        command.Parameters.AddWithValue("width", NpgsqlDbType.Interval, bucketWidth);

        var results = new List<HistorianBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new HistorianBucket(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(2),
                (int)reader.GetInt64(1),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return results;
    }

    public async Task<DateTimeOffset?> LastIngestedAtAsync(CancellationToken cancellationToken)
    {
        // The hypertable is partitioned by source time, not ingestion time, so an unbounded
        // max(ingested_at) would read every chunk ever written. Recent source times are
        // tried first and the window widened only when they hold nothing. A sample that
        // arrived late with an old source time can be missed by a narrow window; the
        // answer is then earlier than the truth, which widens the recorded outage rather
        // than hiding any of it.
        foreach (var window in new TimeSpan?[] { TimeSpan.FromDays(1), TimeSpan.FromDays(30), null })
        {
            await using var command = _dataSource.CreateCommand(
                window is null
                    ? "SELECT max(ingested_at) FROM tag_sample"
                    : "SELECT max(ingested_at) FROM tag_sample WHERE source_time >= now() - @window");

            if (window is { } span)
            {
                command.Parameters.AddWithValue("window", span);
            }

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is DateTime utc)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            }

            if (result is DateTimeOffset value)
            {
                return value;
            }
        }

        return null;
    }

    private static async Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter writer,
        T? value,
        NpgsqlDbType type,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteAsync(value, type, cancellationToken).ConfigureAwait(false);
        }
    }
}
