using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// How a <see cref="TagValue"/> is written on the wire (ADR-0003, ADR-0017).
/// </summary>
/// <remarks>
/// One encoder and one decoder for every payload that carries a value, rather than one per payload.
/// The sample payload and the write request must agree about what <c>{"kind":"numeric","numeric":4.2}</c>
/// means down to the last field, and two copies of that agreement is two places for it to drift —
/// a drift that would read a value of one kind as a value of another, which is the class of mistake
/// ADR-0003 exists to prevent.
/// </remarks>
internal static class TagValueJson
{
    /// <summary>The value as a JSON object, or <c>{"kind":"none"}</c> when there is no value.</summary>
    internal static JsonObject Write(TagValue? value) => value switch
    {
        TagValue.Numeric numeric => new JsonObject { ["kind"] = "numeric", ["numeric"] = numeric.Value },
        TagValue.Boolean boolean => new JsonObject { ["kind"] = "boolean", ["boolean"] = boolean.Value },
        TagValue.Text text => new JsonObject { ["kind"] = "text", ["text"] = text.Value },
        TagValue.Discrete discrete => new JsonObject { ["kind"] = "discrete", ["code"] = discrete.Code, ["label"] = discrete.Label },
        _ => new JsonObject { ["kind"] = "none" },
    };

    /// <summary>
    /// The value a JSON object states, or null when it does not state one this build reads.
    /// </summary>
    /// <remarks>
    /// A <c>"none"</c> is a value of no kind and reads as null with a true answer — it is a legal
    /// thing for a sample to carry when its quality is not Good, and the caller decides whether it
    /// may (the sample reader refuses a Good sample without a value; a write never accepts one).
    /// </remarks>
    internal static bool TryRead(JsonNode? node, out TagValue? value)
    {
        value = null;

        if (node is not JsonObject body || body["kind"]?.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        switch (body["kind"]!.GetValue<string>())
        {
            case "none":
                return true;
            case "numeric" when body["numeric"]?.GetValueKind() == JsonValueKind.Number:
                var number = body["numeric"]!.GetValue<double>();
                if (!double.IsFinite(number))
                {
                    return false;
                }

                value = new TagValue.Numeric(number);
                return true;
            case "boolean" when body["boolean"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                value = new TagValue.Boolean(body["boolean"]!.GetValue<bool>());
                return true;
            case "text" when body["text"]?.GetValueKind() == JsonValueKind.String:
                value = new TagValue.Text(body["text"]!.GetValue<string>());
                return true;
            case "discrete" when body["code"] is JsonValue codeNode && codeNode.TryGetValue<int>(out var code):
                var label = body["label"]?.GetValueKind() == JsonValueKind.String ? body["label"]!.GetValue<string>() : null;
                value = new TagValue.Discrete(code, label);
                return true;
            default:
                return false;
        }
    }
}
