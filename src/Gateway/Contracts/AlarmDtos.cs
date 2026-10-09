using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>Wire form of one standing alarm.</summary>
/// <param name="SiteId">
/// The Site the alarm was raised in, fixed at the raise (ADR-0013). Lets a client that
/// receives alarms one Site at a time replace that Site's list without touching the others.
/// </param>
/// <param name="AcknowledgedBy">
/// The username of whoever acknowledged it, as it read at the time. Carried on the alarm
/// since the journal made it part of alarm state (ADR-0013); the audit trail still records
/// it separately (ADR-0011).
/// </param>
/// <param name="ShelvedUntilUtc">When a shelf ends; set only while shelved.</param>
public sealed record AlarmDto(
    Guid OccurrenceId,
    Guid DefinitionId,
    Guid TagId,
    Guid SiteId,
    string TagPath,
    string Limit,
    double LimitValue,
    double ValueAtRaise,
    string? UnitSymbol,
    DateTimeOffset RaisedAtUtc,
    string State,
    DateTimeOffset? AcknowledgedAtUtc,
    string? AcknowledgedBy,
    DateTimeOffset? ClearedAtUtc,
    DateTimeOffset? ShelvedUntilUtc,
    bool DetectedAfterRestart,
    string? Priority)
{
    public static AlarmDto From(Alarm alarm) => new(
        alarm.OccurrenceId,
        alarm.DefinitionId,
        alarm.TagId,
        alarm.SiteId,
        alarm.TagPath,
        alarm.Limit.ToString(),
        alarm.LimitValue,
        alarm.ValueAtRaise,
        alarm.UnitSymbol,
        alarm.RaisedAtUtc,
        alarm.State.ToString(),
        alarm.AcknowledgedAtUtc,
        alarm.AcknowledgedBy?.Username,
        alarm.ClearedAtUtc,
        alarm.ShelvedUntilUtc,
        alarm.DetectedAfterRestart,

        // Null travels as null and means **not yet rationalised** (ADR-0034 §2). A client that reads
        // it as "Low" would be inventing an assessment nobody made, which is why the server never
        // substitutes one here.
        alarm.Priority?.ToString());
}

/// <summary>How long to shelve an alarm for: at most the configured maximum (ADR-0013).</summary>
public sealed record ShelveRequest(int? DurationMinutes);

/// <summary>Wire form of a configured threshold.</summary>
/// <param name="OnDelaySeconds">
/// How long the condition must hold before the alarm is raised, or null for none (ADR-0025 §2). Sent
/// as seconds rather than as a duration because that is what the column holds and what an author
/// types, so the client shows it in the same unit it was entered in.
/// </param>
/// <param name="Deadband">
/// How far a value must come back past the limit before the alarm clears, or null for none
/// (ADR-0025 §3).
/// </param>
public sealed record AlarmDefinitionDto(
    Guid Id,
    Guid TagId,
    double? HighLimit,
    double? LowLimit,
    double? OnDelaySeconds,
    double? Deadband,
    string? Priority)
{
    public static AlarmDefinitionDto From(AlarmDefinition definition) =>
        new(
            definition.Id,
            definition.TagId,
            definition.HighLimit,
            definition.LowLimit,
            definition.OnDelaySeconds?.TotalSeconds,
            definition.Deadband,
            definition.Priority?.ToString());
}

/// <summary>A threshold as submitted from the configuration UI.</summary>
public sealed record SaveAlarmDefinitionRequest(
    double? HighLimit,
    double? LowLimit,
    double? OnDelaySeconds = null,
    double? Deadband = null,

    // **Optional, and omitting it means "not yet rationalised"** rather than "leave it as it was".
    // A screen that sends the whole definition on save must send this too; the alternative -- an
    // absent field meaning "unchanged" -- would make it impossible to ever clear a priority, which
    // is a thing a rationalisation session legitimately does.
    string? Priority = null);

/// <summary>Wire form of one journal entry (ADR-0013).</summary>
/// <param name="SiteId">
/// Null on the engine's own events, which belong to no Site — not on an alarm event,
/// where migration 0009's CHECK requires one.
/// </param>
/// <param name="GapFromUtc">
/// On <c>EvaluationStarted</c>, how far back the Gateway is known to have been alive;
/// on <c>JournalGap</c>, the start of the window whose transitions went unrecorded;
/// on <c>SamplesLost</c>, the source time of the oldest sample the source dropped.
/// </param>
/// <param name="DeviceId">The device a <c>SamplesLost</c> or <c>SourceClockSkew</c> is about.</param>
/// <param name="LostSamples">On <c>SamplesLost</c>, how many samples the source dropped.</param>
/// <param name="ClockSkewSeconds">
/// On <c>SourceClockSkew</c>, the source's clock minus the Gateway's: positive when it runs ahead.
/// </param>
public sealed record AlarmEventDto(
    string Type,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? SourceTimeUtc,
    Guid? OccurrenceId,
    Guid? DefinitionId,
    Guid? TagId,
    Guid? SiteId,
    string? TagPath,
    string? Limit,
    double? LimitValue,
    double? Value,
    string? UnitSymbol,
    string? ActorUsername,
    bool DetectedAfterRestart,
    DateTimeOffset? ShelvedUntilUtc,
    string? Reason,
    DateTimeOffset? GapFromUtc,
    DateTimeOffset? GapUntilUtc,
    int? UnrecordedTransitions,
    Guid? DeviceId,
    long? LostSamples,
    double? ClockSkewSeconds)
{
    public static AlarmEventDto From(AlarmEvent journalEvent) => new(
        journalEvent.Type.ToString(),
        journalEvent.RecordedAtUtc,
        journalEvent.SourceTimeUtc,
        journalEvent.OccurrenceId,
        journalEvent.DefinitionId,
        journalEvent.TagId,
        journalEvent.SiteId,
        journalEvent.TagPath,
        journalEvent.Limit?.ToString(),
        journalEvent.LimitValue,
        journalEvent.Value,
        journalEvent.UnitSymbol,
        journalEvent.Actor?.Username,
        journalEvent.DetectedAfterRestart,
        journalEvent.ShelvedUntilUtc,
        journalEvent.Reason,
        journalEvent.GapFromUtc,
        journalEvent.GapUntilUtc,
        journalEvent.UnrecordedTransitions,
        journalEvent.DeviceId,
        journalEvent.LostSamples,
        journalEvent.ClockSkewSeconds);
}
