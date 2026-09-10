using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>Wire form of one standing alarm.</summary>
/// <param name="AcknowledgedAtUtc">
/// When it was acknowledged, with no record of by whom. Users arrive in Phase 5;
/// attributing this to anyone now would be a fiction the client would then display.
/// </param>
public sealed record AlarmDto(
    Guid DefinitionId,
    Guid TagId,
    string TagPath,
    string Limit,
    double LimitValue,
    double ValueAtRaise,
    string? UnitSymbol,
    DateTimeOffset RaisedAtUtc,
    string State,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ClearedAtUtc)
{
    public static AlarmDto From(Alarm alarm) => new(
        alarm.DefinitionId,
        alarm.TagId,
        alarm.TagPath,
        alarm.Limit.ToString(),
        alarm.LimitValue,
        alarm.ValueAtRaise,
        alarm.UnitSymbol,
        alarm.RaisedAtUtc,
        alarm.State.ToString(),
        alarm.AcknowledgedAtUtc,
        alarm.ClearedAtUtc);
}

/// <summary>Wire form of a configured threshold.</summary>
public sealed record AlarmDefinitionDto(Guid Id, Guid TagId, double? HighLimit, double? LowLimit)
{
    public static AlarmDefinitionDto From(AlarmDefinition definition) =>
        new(definition.Id, definition.TagId, definition.HighLimit, definition.LowLimit);
}

/// <summary>A threshold as submitted from the configuration UI.</summary>
public sealed record SaveAlarmDefinitionRequest(double? HighLimit, double? LowLimit);
