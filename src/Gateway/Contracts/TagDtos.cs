using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>
/// Wire form of the <see cref="TagValue"/> union. The kind is explicit and only the
/// matching field is populated, so a client can tell a real <c>false</c> or <c>0</c>
/// from an absent value (ADR-0003).
/// </summary>
public sealed record TagValueDto(
    string Kind,
    double? Numeric = null,
    bool? Boolean = null,
    string? Text = null,
    int? Code = null,
    string? Label = null)
{
    /// <summary>The wire form of "no value", used when a reading carried none.</summary>
    public static readonly TagValueDto None = new("none");

    public static TagValueDto From(TagValue? value) => value switch
    {
        null => None,
        // JSON has no way to express NaN or infinity. A non-finite reading is a fault, not
        // a measurement, so it goes out as "no value" — otherwise serialisation throws
        // part-way through a response whose headers have already been sent.
        TagValue.Numeric n => double.IsFinite(n.Value)
            ? new TagValueDto("numeric", Numeric: n.Value)
            : None,
        TagValue.Boolean b => new TagValueDto("boolean", Boolean: b.Value),
        TagValue.Text t => new TagValueDto("text", Text: t.Value),
        TagValue.Discrete d => new TagValueDto("discrete", Code: d.Code, Label: d.Label),
        _ => throw new NotSupportedException($"Unmapped tag value kind: {value.Kind}."),
    };
}

/// <summary>Wire form of a live tag value.</summary>
/// <param name="TagId">The stable identity clients bind to (ADR-0001).</param>
/// <param name="Path">Derived display label — for showing to an operator, never for binding.</param>
public sealed record TagSnapshotDto(
    Guid TagId,
    string Path,
    TagValueDto Value,
    DateTimeOffset SourceTimestampUtc,
    string Quality,
    string? UnitSymbol)
{
    public static TagSnapshotDto From(TagSnapshot snapshot) => new(
        snapshot.TagId,
        snapshot.Path,
        TagValueDto.From(snapshot.Value),
        snapshot.SourceTimestampUtc,
        snapshot.Quality.ToString(),
        snapshot.UnitSymbol);
}

/// <summary>Wire form of one historized sample.</summary>
public sealed record HistorySampleDto(
    TagValueDto Value,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset IngestedAtUtc,
    string Quality);
