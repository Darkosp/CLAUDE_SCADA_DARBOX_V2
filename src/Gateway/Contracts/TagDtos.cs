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
/// <param name="SourceTimestampUtc">When the value was measured; null only if nothing ever was.</param>
/// <param name="NoDataSinceUtc">
/// On a tag that has never received anything: when the Gateway began listening for it — an
/// observed time, for "no data since", never a measurement time (ADR-0016).
/// </param>
public sealed record TagSnapshotDto(
    Guid TagId,
    string Path,
    TagValueDto Value,
    DateTimeOffset? SourceTimestampUtc,
    string Quality,
    string? UnitSymbol,
    DateTimeOffset? NoDataSinceUtc)
{
    public static TagSnapshotDto From(TagSnapshot snapshot) => new(
        snapshot.TagId,
        snapshot.Path,
        TagValueDto.From(snapshot.Value),
        snapshot.SourceTimestampUtc,
        snapshot.Quality.ToString(),
        snapshot.UnitSymbol,
        snapshot.NoDataSinceUtc);
}

/// <summary>
/// A tag's history together with the names it was recorded under.
/// </summary>
/// <param name="TagName">
/// Resolved even for a deleted tag, so old data reads as a name rather than an
/// identifier. Null only if the tag never existed.
/// </param>
/// <param name="IsDeleted">
/// Whether the tag or its device has been deleted. The history is still real; the
/// client can say so rather than presenting it as live configuration.
/// </param>
/// <param name="Samples">
/// Every reading in the window. Populated exactly when the request asked for no
/// <c>points</c>, because reduction is requested rather than applied silently (ADR-0029 §3).
/// </param>
/// <param name="BucketMilliseconds">
/// How wide one bucket is, when the answer is a reduction, and null when it is a reading. It is
/// how the response says which of the two it is, and what resolution it chose.
/// </param>
/// <param name="Buckets">
/// The window reduced to buckets — empty when <paramref name="Samples"/> is the answer. **One or
/// the other, never a mixture**: a payload holding both would be one whose completeness a reader
/// has to work out (ADR-0029 §3).
/// </param>
public sealed record TagHistoryDto(
    Guid TagId,
    string? TagName,
    string? DeviceName,
    bool IsDeleted,
    IReadOnlyList<HistorySampleDto> Samples,
    long? BucketMilliseconds,
    IReadOnlyList<HistoryBucketDto> Buckets);

/// <summary>Wire form of one historized sample.</summary>
public sealed record HistorySampleDto(
    TagValueDto Value,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset IngestedAtUtc,
    string Quality);

/// <summary>
/// Wire form of one bucket of a reduced history (ADR-0029).
/// </summary>
/// <param name="StartUtc">
/// The start of the stretch, on the grid the caller's own window set, so the first bucket begins
/// exactly at the window's edge.
/// </param>
/// <param name="LastUtc">
/// The newest reading in it — the only field that can answer how fresh the trend is, because
/// <paramref name="StartUtc"/> is the edge of the stretch rather than a measurement time, and an age
/// decided on it would call a live trend stale up to one bucket early.
/// </param>
/// <param name="Count">
/// How many readings were read in it, whatever their quality, so a bucket the device answered with
/// nothing but Bad is a hole with a count rather than a quiet bucket. Never zero: a stretch nothing
/// was measured in is absent from the list.
/// </param>
/// <param name="Low">
/// The extremes of the readings a trend would have plotted — Good, numeric and finite — or null
/// when the bucket held none of those. Never an average, and never a substituted value.
/// </param>
/// <param name="High">The other extreme, or null for the same reason as <paramref name="Low"/>.</param>
public sealed record HistoryBucketDto(
    DateTimeOffset StartUtc,
    DateTimeOffset LastUtc,
    int Count,
    double? Low,
    double? High);
