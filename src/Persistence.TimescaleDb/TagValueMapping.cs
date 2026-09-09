using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Maps the closed <see cref="TagValue"/> union onto the typed columns of the schema
/// and back (ADR-0003). Kept in one place so that adding a kind is a single, visible
/// change rather than a scattered one.
/// </summary>
internal static class TagValueMapping
{
    /// <summary>Splits a value into the typed columns, leaving the others null.</summary>
    internal static (double? Numeric, bool? Boolean, string? Text, int? Code, string? Label) ToColumns(TagValue? value) =>
        value switch
        {
            null => (null, null, null, null, null),
            TagValue.Numeric n => (n.Value, null, null, null, null),
            TagValue.Boolean b => (null, b.Value, null, null, null),
            TagValue.Text t => (null, null, t.Value, null, null),
            TagValue.Discrete d => (null, null, null, d.Code, d.Label),
            _ => throw new NotSupportedException($"Unmapped tag value kind: {value.Kind}."),
        };

    /// <summary>Rebuilds a value from the discriminator and the typed columns, or null when there was none.</summary>
    internal static TagValue? FromColumns(
        TagValueKind? kind,
        double? numeric,
        bool? boolean,
        string? text,
        int? code,
        string? label) =>
        kind switch
        {
            null => null,
            TagValueKind.Numeric => new TagValue.Numeric(
                numeric ?? throw new InvalidDataException("Numeric sample has no numeric_value.")),
            TagValueKind.Boolean => new TagValue.Boolean(
                boolean ?? throw new InvalidDataException("Boolean sample has no boolean_value.")),
            TagValueKind.Text => new TagValue.Text(
                text ?? throw new InvalidDataException("Text sample has no text_value.")),
            TagValueKind.Discrete => new TagValue.Discrete(
                code ?? throw new InvalidDataException("Discrete sample has no discrete_code."),
                label),
            _ => throw new NotSupportedException($"Unmapped tag value kind: {kind}."),
        };
}
