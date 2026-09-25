using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The wire format (ADR-0017): exactly ADR-0003's fields and a version, the sender's clock, and the
/// losses it reports. What cannot be read is refused and said, never guessed into a value or a time.
/// </summary>
public sealed class SamplePayloadTests
{
    private static readonly Guid Pressure = new("33333333-3333-4333-8333-333333333301");
    private static readonly Guid Running = new("33333333-3333-4333-8333-333333333302");

    private static readonly IReadOnlyDictionary<Guid, DriverTag> Tags = new Dictionary<Guid, DriverTag>
    {
        [Pressure] = new(Pressure, "pressure", TagValueKind.Numeric),
        [Running] = new(Running, "running", TagValueKind.Boolean),
    };

    private static readonly DateTimeOffset MeasuredAt = new(2026, 9, 24, 10, 15, 30, 250, TimeSpan.Zero);
    private static readonly DateTimeOffset SentAt = MeasuredAt.AddMinutes(40);

    [Fact]
    public void What_is_written_reads_back_exactly()
    {
        var samples = new[]
        {
            new TagReading(Pressure, new TagValue.Numeric(4.2), MeasuredAt, Quality.Good),
            new TagReading(Running, new TagValue.Boolean(true), MeasuredAt.AddSeconds(1), Quality.Uncertain),
            new TagReading(Pressure, null, MeasuredAt.AddSeconds(2), Quality.Bad),
        };

        var losses = new[] { new SourceLoss(Guid.NewGuid(), 1200, MeasuredAt.AddHours(-3), MeasuredAt.AddHours(-2)) };

        var result = SamplePayload.Read(SamplePayload.Write(samples, losses, SentAt), Tags);

        Assert.Null(result.Refusal);
        Assert.Empty(result.Rejected);
        Assert.Equal(samples, result.Accepted);
        Assert.Equal(losses, result.Lost);
        Assert.Equal(SentAt, result.SentAtUtc);
    }

    [Fact]
    public void A_message_without_the_senders_clock_is_refused_whole()
    {
        var json = $$"""{ "version": 2, "samples": [], "lost": [] }""";

        var result = SamplePayload.Read(json, Tags);

        Assert.Contains("sentAtUtc", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_without_a_lost_list_is_refused_whole()
    {
        // "Nothing was lost" is an empty list. An absent one could be a sender that forgot, and
        // reading it as nothing lost would make its losses silent.
        var json = $$"""{ "version": 2, "sentAtUtc": "2026-09-24T10:55:30Z", "samples": [] }""";

        var result = SamplePayload.Read(json, Tags);

        Assert.Contains("no lost array", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "count": 5, "fromSourceUtc": "2026-09-24T01:00:00Z", "toSourceUtc": "2026-09-24T02:00:00Z" }""")]
    [InlineData("""{ "lossId": "5d0c1b0e-8b8e-4a51-9a39-1f3a3e0f7a01", "count": 0, "fromSourceUtc": "2026-09-24T01:00:00Z", "toSourceUtc": "2026-09-24T02:00:00Z" }""")]
    [InlineData("""{ "lossId": "5d0c1b0e-8b8e-4a51-9a39-1f3a3e0f7a01", "count": 5, "fromSourceUtc": "2026-09-24T03:00:00Z", "toSourceUtc": "2026-09-24T02:00:00Z" }""")]
    [InlineData("""{ "lossId": "5d0c1b0e-8b8e-4a51-9a39-1f3a3e0f7a01", "count": 5, "fromSourceUtc": "2026-09-24T01:00:00", "toSourceUtc": "2026-09-24T02:00:00Z" }""")]
    public void A_malformed_loss_report_is_refused_and_said_while_the_samples_beside_it_are_kept(string loss)
    {
        var json = Message([loss], Sample(Pressure, "Good", """{ "kind": "numeric", "numeric": 4.2 }"""));

        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Lost);
        Assert.Contains(result.Rejected, reason => reason.StartsWith("lost 0:", StringComparison.Ordinal));
        Assert.Single(result.Accepted);
    }

    [Fact]
    public void A_message_of_another_version_is_refused_whole()
    {
        // Version 1 had no lost list: read as if it had an empty one, its sender's losses would
        // be silent. Refused, and the reason says so in the log.
        var json = SamplePayload.Write([new TagReading(Pressure, new TagValue.Numeric(1), MeasuredAt, Quality.Good)], [], SentAt)
            .Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);

        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Accepted);
        Assert.Contains("version 1", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"samples\": []}")]
    public void What_is_not_a_message_is_refused_rather_than_thrown(string json)
    {
        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Accepted);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void Good_without_a_value_is_refused_and_Bad_without_one_is_accepted_as_no_value()
    {
        var json = Message(
            Sample(Pressure, "Good", """{ "kind": "none" }"""),
            Sample(Pressure, "Bad", """{ "kind": "none" }"""));

        var result = SamplePayload.Read(json, Tags);

        // A Good sample with nothing in it is a contradiction — never read as zero.
        Assert.Contains(result.Rejected, reason => reason.Contains("Good quality with no value", StringComparison.Ordinal));
        var bad = Assert.Single(result.Accepted);
        Assert.Equal(Quality.Bad, bad.Quality);
        Assert.Null(bad.Value);
    }

    [Fact]
    public void A_source_time_without_an_offset_is_refused()
    {
        // Read in this server's zone, it would be a time the reader made up.
        var json = Message(Sample(Pressure, "Good", """{ "kind": "numeric", "numeric": 4.2 }""", "2026-09-24T10:15:30"));

        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Accepted);
        Assert.Contains(result.Rejected, reason => reason.Contains("no offset", StringComparison.Ordinal));
    }

    [Fact]
    public void An_offset_time_is_kept_as_the_same_instant()
    {
        var json = Message(Sample(Pressure, "Good", """{ "kind": "numeric", "numeric": 4.2 }""", "2026-09-24T12:15:30.25+02:00"));

        var sample = Assert.Single(SamplePayload.Read(json, Tags).Accepted);

        Assert.Equal(MeasuredAt, sample.SourceTimestampUtc);
    }

    [Fact]
    public void A_sample_for_a_tag_the_device_does_not_have_is_not_accepted()
    {
        var json = Message(Sample(Guid.NewGuid(), "Good", """{ "kind": "numeric", "numeric": 4.2 }"""));

        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Accepted);
        Assert.Equal(1, result.UnknownTags);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_for_its_tag_is_refused()
    {
        var json = Message(Sample(Running, "Good", """{ "kind": "numeric", "numeric": 1 }"""));

        var result = SamplePayload.Read(json, Tags);

        Assert.Empty(result.Accepted);
        Assert.Contains(result.Rejected, reason => reason.Contains("wrong kind", StringComparison.Ordinal));
    }

    private static string Message(params string[] samples) => Message([], samples);

    private static string Message(string[] lost, params string[] samples) =>
        $$"""{ "version": 2, "sentAtUtc": "2026-09-24T10:55:30Z", "samples": [{{string.Join(",", samples)}}], "lost": [{{string.Join(",", lost)}}] }""";

    private static string Sample(Guid tagId, string quality, string value, string time = "2026-09-24T10:15:30.25Z") =>
        $$"""{ "tagId": "{{tagId}}", "sourceTimestampUtc": "{{time}}", "quality": "{{quality}}", "value": {{value}} }""";
}
