using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudAgent.Core.Tools;

/// <summary>
/// Shrinks oversized content without corrupting it. JSON is truncated structurally — string
/// leaves are clipped, structure and keys are preserved, output stays valid JSON — because a
/// blunt character cut on JSON produces an unparseable fragment that can wedge the whole
/// conversation. Non-JSON falls back to a marked plain cut.
/// </summary>
public static class JsonTruncator
{
    /// <summary>Reserved headroom so the truncation marker itself never busts the budget.</summary>
    private const int MarkerReserve = 64;

    /// <summary>Truncate <paramref name="content"/> to roughly <paramref name="maxChars"/>. A
    /// non-positive budget disables truncation.</summary>
    public static string Truncate(string content, int maxChars)
    {
        if (maxChars <= 0 || content.Length <= maxChars)
            return content;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return PlainCut(content, maxChars);
        }

        if (root is null)
            return PlainCut(content, maxChars);

        // Clip string leaves progressively harder until the whole document fits. Each pass
        // starts from the original parse so markers don't compound.
        for (var cap = 2048; cap >= 16; cap /= 2)
        {
            var attempt = root.DeepClone();
            ClipLeaves(ref attempt, cap);
            var json = attempt!.ToJsonString();
            if (json.Length <= maxChars)
                return json;
        }

        // Structure alone exceeds the budget (e.g. an enormous array) — cut, but keep the
        // marker so the model knows it is looking at a fragment.
        return PlainCut(root.ToJsonString(), maxChars);
    }

    private static void ClipLeaves(ref JsonNode? node, int cap)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var child = obj[key];
                    ClipLeaves(ref child, cap);
                    obj[key] = child;
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    ClipLeaves(ref child, cap);
                    array[i] = child;
                }
                break;

            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > cap:
                node = JsonValue.Create($"{text[..cap]}…[+{text.Length - cap} chars]");
                break;
        }
    }

    private static string PlainCut(string content, int maxChars)
    {
        var keep = maxChars > MarkerReserve ? maxChars - MarkerReserve : maxChars;
        return $"{content[..keep]}\n…[truncated, {keep} of {content.Length} chars shown]";
    }
}
