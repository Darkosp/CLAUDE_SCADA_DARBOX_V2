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
