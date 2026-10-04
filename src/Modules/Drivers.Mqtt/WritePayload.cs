using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// The messages that carry a tag write to the edge that reads the device, and its result back
/// (ADR-0023).
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// request:  { "version": 1, "writeId": "…", "tagId": "…", "value": { "kind": "numeric", "numeric": 4.5 } }
/// written:  { "version": 1, "writeId": "…", "tagId": "…", "outcome": "written" }
/// failed:   { "version": 1, "writeId": "…", "tagId": "…", "outcome": "failed", "reason": "…" }
/// </code>
/// </para>
/// <para>
/// <b>This is the only payload that travels from the cloud to a plant to ask for something.</b>
/// Everything else on the link carries a measurement or a fact backwards, or configuration
/// forwards. It is also the only one that must not be retained and must not be buffered — a late
/// sample is still true of its moment, and a late command is a request to change a plant after the
/// reason for it has passed (ADR-0023 §3). Nothing in this file can enforce that; it is enforced by
/// how the two ends publish and subscribe, and said here because a reader who changes one of them
/// should meet the reason first.
/// </para>
/// <para>
/// <b>The id is what makes the answer the answer.</b> The cloud may have several writes in flight to
/// the same edge, and an edge may answer out of order — so a reply carries the id it answers, and a
/// reply naming an id that is not in flight is ignored rather than interpreted. Without it, a
/// result could be attributed to the wrong write, which on a plant means believing something was
/// set when it was not.
/// </para>
/// <para>
/// A tag is named by its stable id (ADR-0001) and a value travels exactly as ADR-0003 defines it,
/// through the same encoder the sample payload uses — a write of the wrong kind for its tag is
/// refused by the edge's driver, not silently coerced here.
/// </para>
/// </remarks>
public static class WritePayload
{
    /// <summary>The format version this build writes and reads. Version 1 is the first.</summary>
    public const int Version = 1;

    /// <summary>The outcome of a write the edge carried out.</summary>
    public const string Written = "written";

    /// <summary>The outcome of a write the edge could not carry out; <c>reason</c> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Writes one write request.</summary>
    public static string WriteRequest(Guid writeId, Guid tagId, TagValue value) =>
        new JsonObject
        {
            ["version"] = Version,
            ["writeId"] = writeId.ToString(),
            ["tagId"] = tagId.ToString(),
            ["value"] = TagValueJson.Write(value),
        }.ToJsonString();

    /// <summary>Writes the result of one write.</summary>
    public static string WriteResult(Guid writeId, Guid tagId, bool written, string? reason = null) =>
        new JsonObject
        {
            ["version"] = Version,
            ["writeId"] = writeId.ToString(),
            ["tagId"] = tagId.ToString(),
            ["outcome"] = written ? Written : Failed,
            ["reason"] = written ? null : reason ?? "the edge did not say why",
        }.ToJsonString();

    /// <summary>Reads one write request, or why it cannot be read.</summary>
    public static WriteRequestResult ReadRequest(string json)
    {
        if (ReadEnvelope(json, out var message, out var refusal) is false)
        {
            return WriteRequestResult.Refused(refusal!);
        }

        var body = (JsonObject)message!;

        if (!TryReadId(body["writeId"], out var writeId))
        {
            return WriteRequestResult.Refused("no write id");
        }

        if (!TryReadId(body["tagId"], out var tagId))
        {
            return WriteRequestResult.Refused("no tag id");
        }

        if (!TagValueJson.TryRead(body["value"], out var value) || value is null)
        {
            // A write with no value is not a write. `{"kind":"none"}` is a legal sample and never a
            // legal command: there is nothing to set a tag to.
            return WriteRequestResult.Refused("no writable value");
        }

        return new WriteRequestResult(writeId, tagId, value, Refusal: null);
    }

    /// <summary>Reads one write result, or why it cannot be read.</summary>
    public static WriteResultResult ReadResult(string json)
    {
        if (ReadEnvelope(json, out var message, out var refusal) is false)
        {
            return WriteResultResult.Refused(refusal!);
        }

        var body = (JsonObject)message!;

        if (!TryReadId(body["writeId"], out var writeId))
        {
            return WriteResultResult.Refused("no write id");
        }

        if (!TryReadId(body["tagId"], out var tagId))
        {
            return WriteResultResult.Refused("no tag id");
        }

        if (body["outcome"]?.GetValueKind() != JsonValueKind.String)
        {
            return WriteResultResult.Refused("no outcome");
        }

        var outcome = body["outcome"]!.GetValue<string>();
        if (outcome is not (Written or Failed))
        {
            return WriteResultResult.Refused($"outcome '{outcome}' is not understood");
        }

        var reason = body["reason"]?.GetValueKind() == JsonValueKind.String
            ? body["reason"]!.GetValue<string>()
            : null;

        // A failed write with no reason would be a refusal an operator cannot act on, which is the
        // shape ADR-0003 refuses: say why, or say nothing happened.
        if (outcome == Failed && string.IsNullOrWhiteSpace(reason))
        {
            return WriteResultResult.Refused("a failed write with no reason");
        }

        return new WriteResultResult(writeId, tagId, outcome == Written, reason, Refusal: null);
    }

    /// <summary>
    /// The version and the shape both messages share: an object, with a version this build reads.
    /// </summary>
    private static bool ReadEnvelope(string json, out JsonNode? message, out string? refusal)
    {
        message = null;
        refusal = null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            refusal = $"not JSON: {exception.Message}";
            return false;
        }

        if (root is not JsonObject body)
        {
            refusal = "not a JSON object";
            return false;
        }

        if (body["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version))
        {
            refusal = "no version";
            return false;
        }

        if (version != Version)
        {
            refusal = $"version {version} is not understood (this build reads version {Version})";
            return false;
        }

        message = body;
        return true;
    }

    private static bool TryReadId(JsonNode? node, out Guid id)
    {
        id = Guid.Empty;

        return node?.GetValueKind() == JsonValueKind.String
            && Guid.TryParse(node.GetValue<string>(), out id);
    }
}

/// <summary>What one write request yielded.</summary>
/// <param name="WriteId">The id the reply must carry; <see cref="Guid.Empty"/> on a refusal.</param>
/// <param name="TagId">The tag to set; <see cref="Guid.Empty"/> on a refusal.</param>
/// <param name="Value">What to set it to; null on a refusal.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record WriteRequestResult(
    Guid WriteId,
    Guid TagId,
    TagValue? Value,
    string? Refusal)
{
    public static WriteRequestResult Refused(string reason) => new(Guid.Empty, Guid.Empty, null, reason);
}

/// <summary>What one write result yielded.</summary>
/// <param name="WriteId">The request this answers.</param>
/// <param name="TagId">The tag it was about.</param>
/// <param name="Written">True when the driver accepted it.</param>
/// <param name="Reason">Why not, when <paramref name="Written"/> is false.</param>
/// <param name="Refusal">Why the whole message was refused, or null if it was read.</param>
public sealed record WriteResultResult(
    Guid WriteId,
    Guid TagId,
    bool Written,
    string? Reason,
    string? Refusal)
{
    public static WriteResultResult Refused(string reason) => new(Guid.Empty, Guid.Empty, false, null, reason);
}
