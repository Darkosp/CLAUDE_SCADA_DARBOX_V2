using Npgsql;
using NpgsqlTypes;
using ScadaDarbox.Core.Alarms;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// The append-only <c>alarm_event</c> table behind core's <see cref="IAlarmJournal"/>
/// (ADR-0013). Plain Npgsql, as the historian: it is an event log, not configuration, so
/// ADR-0008's Dapper choice does not reach it.
/// </summary>
public sealed class AlarmJournal : IAlarmJournal
{
    private const string Columns = """
        event_type, occurrence_id, definition_id, tag_id, site_id, source_time, recorded_at,
        actor_user_id, actor_username, alarm_limit, limit_value, value, unit_symbol, tag_path,
        detected_after_restart, shelved_until, reason, gap_from, gap_until, unrecorded_transitions
        """;

    private readonly NpgsqlDataSource _dataSource;

    public AlarmJournal(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task AppendAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            $"""
            INSERT INTO alarm_event ({Columns})
            VALUES (@event_type, @occurrence_id, @definition_id, @tag_id, @site_id, @source_time, @recorded_at,
                    @actor_user_id, @actor_username, @alarm_limit, @limit_value, @value, @unit_symbol, @tag_path,
                    @detected_after_restart, @shelved_until, @reason, @gap_from, @gap_until, @unrecorded_transitions)
            """);

        var parameters = command.Parameters;
        parameters.AddWithValue("event_type", alarmEvent.Type.ToString());
        Add(parameters, "occurrence_id", NpgsqlDbType.Uuid, alarmEvent.OccurrenceId);
        Add(parameters, "definition_id", NpgsqlDbType.Uuid, alarmEvent.DefinitionId);
        Add(parameters, "tag_id", NpgsqlDbType.Uuid, alarmEvent.TagId);
        Add(parameters, "site_id", NpgsqlDbType.Uuid, alarmEvent.SiteId);
        Add(parameters, "source_time", NpgsqlDbType.TimestampTz, alarmEvent.SourceTimeUtc);
        parameters.AddWithValue("recorded_at", NpgsqlDbType.TimestampTz, alarmEvent.RecordedAtUtc);
        Add(parameters, "actor_user_id", NpgsqlDbType.Uuid, alarmEvent.Actor?.UserId);
        Add(parameters, "actor_username", NpgsqlDbType.Text, alarmEvent.Actor?.Username);
        Add(parameters, "alarm_limit", NpgsqlDbType.Text, alarmEvent.Limit?.ToString());
        Add(parameters, "limit_value", NpgsqlDbType.Double, alarmEvent.LimitValue);
        Add(parameters, "value", NpgsqlDbType.Double, alarmEvent.Value);
        Add(parameters, "unit_symbol", NpgsqlDbType.Text, alarmEvent.UnitSymbol);
        Add(parameters, "tag_path", NpgsqlDbType.Text, alarmEvent.TagPath);
        parameters.AddWithValue("detected_after_restart", alarmEvent.DetectedAfterRestart);
        Add(parameters, "shelved_until", NpgsqlDbType.TimestampTz, alarmEvent.ShelvedUntilUtc);
        Add(parameters, "reason", NpgsqlDbType.Text, alarmEvent.Reason);
        Add(parameters, "gap_from", NpgsqlDbType.TimestampTz, alarmEvent.GapFromUtc);
        Add(parameters, "gap_until", NpgsqlDbType.TimestampTz, alarmEvent.GapUntilUtc);
        Add(parameters, "unrecorded_transitions", NpgsqlDbType.Integer, alarmEvent.UnrecordedTransitions);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlarmEvent>> ReadOpenAsync(CancellationToken cancellationToken)
    {
        // "Open" is defined by the absence of a Retired event on the occurrence, never by
        // the last event's type (ADR-0013). Engine events have no occurrence and are
        // excluded explicitly: NOT EXISTS over a NULL occurrence would match no Retired
        // row and so let every one of them through.
        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT {Columns}
            FROM alarm_event e
            WHERE e.occurrence_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM alarm_event r
                  WHERE r.occurrence_id = e.occurrence_id AND r.event_type = 'Retired')
            ORDER BY e.id
            """);

        var events = new List<AlarmEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(Read(reader));
        }

        return events;
    }

    internal static AlarmEvent Read(NpgsqlDataReader reader)
    {
        var actorId = Nullable<Guid>(reader, 7);

        return new AlarmEvent
        {
            Type = Enum.Parse<AlarmEventType>(reader.GetString(0)),
            OccurrenceId = Nullable<Guid>(reader, 1),
            DefinitionId = Nullable<Guid>(reader, 2),
            TagId = Nullable<Guid>(reader, 3),
            SiteId = Nullable<Guid>(reader, 4),
            SourceTimeUtc = Nullable<DateTimeOffset>(reader, 5),
            RecordedAtUtc = reader.GetFieldValue<DateTimeOffset>(6),
            Actor = actorId is { } id ? new AlarmActor(id, reader.GetString(8)) : null,
            Limit = reader.IsDBNull(9) ? null : Enum.Parse<AlarmLimit>(reader.GetString(9)),
            LimitValue = Nullable<double>(reader, 10),
            Value = Nullable<double>(reader, 11),
            UnitSymbol = reader.IsDBNull(12) ? null : reader.GetString(12),
            TagPath = reader.IsDBNull(13) ? null : reader.GetString(13),
            DetectedAfterRestart = reader.GetBoolean(14),
            ShelvedUntilUtc = Nullable<DateTimeOffset>(reader, 15),
            Reason = reader.IsDBNull(16) ? null : reader.GetString(16),
            GapFromUtc = Nullable<DateTimeOffset>(reader, 17),
            GapUntilUtc = Nullable<DateTimeOffset>(reader, 18),
            UnrecordedTransitions = Nullable<int>(reader, 19),
        };
    }

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static void Add(NpgsqlParameterCollection parameters, string name, NpgsqlDbType type, object? value) =>
        parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
