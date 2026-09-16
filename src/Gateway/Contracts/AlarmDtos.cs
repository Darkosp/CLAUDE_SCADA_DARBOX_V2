using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>Wire form of one standing alarm.</summary>
/// <param name="SiteId">
/// The Site of the alarm's tag, or null once that tag has left the catalogue. Lets a client
/// that receives alarms one Site at a time replace that Site's list without touching the
/// others.
/// </param>
/// <param name="AcknowledgedAtUtc">
/// When it was acknowledged. Who acknowledged it is recorded in the audit trail
/// (ADR-0011), not carried on the live alarm.
/// </param>
public sealed record AlarmDto(
    Guid DefinitionId,
    Guid TagId,
    Guid? SiteId,
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
    public static AlarmDto From(Alarm alarm, Guid? siteId) => new(
        alarm.DefinitionId,
        alarm.TagId,
        siteId,
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
