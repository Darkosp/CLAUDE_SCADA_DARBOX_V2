using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Drivers.Mqtt.Tests;

/// <summary>
/// The write conversation (ADR-0023): a request that names its own id and the tag to set, and a
/// result that names the request it answers. A message carrying anything else is refused whole
/// rather than guessed at — a write is a command, and half a command is not one.
/// </summary>
public sealed class WritePayloadTests
{
    private static readonly Guid WriteId = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid TagId = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public void A_request_reads_back_as_it_was_written()
    {
        var result = WritePayload.ReadRequest(WritePayload.WriteRequest(WriteId, TagId, new TagValue.Numeric(4.5)));

        Assert.Null(result.Refusal);
        Assert.Equal(WriteId, result.WriteId);
        Assert.Equal(TagId, result.TagId);
        Assert.Equal(new TagValue.Numeric(4.5), result.Value);
    }

    [Theory]
    [InlineData("boolean", true)]
    [InlineData("text", "run")]
    public void Every_kind_a_tag_can_hold_survives_the_round_trip(string kind, object raw)
    {
        // The value encoding is shared with the sample payload (TagValueJson), so this is also the
        // test that the two cannot drift apart about what a kind looks like on the wire.
        TagValue value = kind switch
        {
            "boolean" => new TagValue.Boolean((bool)raw),
            _ => new TagValue.Text((string)raw),
        };

        var result = WritePayload.ReadRequest(WritePayload.WriteRequest(WriteId, TagId, value));

        Assert.Null(result.Refusal);
        Assert.Equal(value, result.Value);
    }

    [Fact]
    public void A_write_result_reads_back_as_it_was_written()
    {
        var written = WritePayload.ReadResult(WritePayload.WriteResult(WriteId, TagId, written: true));
        Assert.Null(written.Refusal);
        Assert.Equal(WriteId, written.WriteId);
        Assert.Equal(TagId, written.TagId);
        Assert.True(written.Written);

        var failed = WritePayload.ReadResult(
            WritePayload.WriteResult(WriteId, TagId, written: false, reason: "the device refused it"));
        Assert.Null(failed.Refusal);
        Assert.False(failed.Written);
        Assert.Equal("the device refused it", failed.Reason);
    }

    [Theory]
    [InlineData("""{"version":1,"tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","value":{"kind":"numeric","numeric":1}}""", "no write id")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","value":{"kind":"numeric","numeric":1}}""", "no tag id")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"not-a-guid","value":{"kind":"numeric","numeric":1}}""", "no tag id")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"}""", "no writable value")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","value":{"kind":"none"}}""", "no writable value")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","value":{"kind":"numeric","numeric":"4.5"}}""", "no writable value")]
    [InlineData("""{"version":2,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","value":{"kind":"numeric","numeric":1}}""", "version 2 is not understood")]
    [InlineData("""{"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"}""", "no version")]
    [InlineData("""not json""", "not JSON")]
    [InlineData("""["a"]""", "not a JSON object")]
    public void A_request_this_build_does_not_read_is_refused_whole(string json, string reason)
    {
        var result = WritePayload.ReadRequest(json);

        Assert.NotNull(result.Refusal);
        Assert.Contains(reason, result.Refusal, StringComparison.Ordinal);
        Assert.Equal(Guid.Empty, result.WriteId);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("""{"version":1,"tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","outcome":"written"}""", "no write id")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","outcome":"written"}""", "no tag id")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"}""", "no outcome")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","outcome":"maybe"}""", "outcome 'maybe' is not understood")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","outcome":"failed"}""", "a failed write with no reason")]
    [InlineData("""{"version":1,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","tagId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","outcome":"failed","reason":"  "}""", "a failed write with no reason")]
    public void A_result_this_build_does_not_read_is_refused_whole(string json, string reason)
    {
        // A failed write with no reason is refused rather than accepted with an empty one: a refusal
        // an operator cannot act on is the shape ADR-0003 exists to prevent.
        var result = WritePayload.ReadResult(json);

        Assert.NotNull(result.Refusal);
        Assert.Contains(reason, result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_and_a_result_are_told_apart_by_their_own_fields()
    {
        // Neither reader accepts the other's message: a result has no value to write, and a request
        // has no outcome. Telling them apart by topic alone would leave a mix-up on one topic
        // looking like a write that silently did nothing.
        var request = WritePayload.WriteRequest(WriteId, TagId, new TagValue.Numeric(1));
        var result = WritePayload.WriteResult(WriteId, TagId, written: true);

        Assert.NotNull(WritePayload.ReadResult(request).Refusal);
        Assert.NotNull(WritePayload.ReadRequest(result).Refusal);
    }
}
