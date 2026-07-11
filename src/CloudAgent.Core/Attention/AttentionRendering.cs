using System.Collections.Generic;
using System.Linq;
using System.Text;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Attention;

/// <summary>
/// The prompt-block rendering shared across the attention axis. Kept as pure functions over the
/// raw materials (persona, resident entries, state, summary, hits) so any <see cref="IAttentionManager"/>
/// can render the same way — in particular, a metacognitive gate can re-render the volatile block
/// after suppressing hits, instead of re-implementing the format.
/// </summary>
public static class AttentionRendering
{
    /// <summary>The stable system prefix: persona plus standing (resident) memories.</summary>
    public static string System(string persona, IReadOnlyList<MemoryEntry> residents)
    {
        var sb = new StringBuilder(persona);

        if (residents.Count > 0)
            sb.Append("\n\n## Standing memories\n")
              .Append("(These earned permanent residence through measured usefulness.)\n")
              .Append(string.Join("\n", residents.Select(e => $"- {e.Content}")));

        return sb.ToString();
    }

    /// <summary>The volatile per-turn context: operational state, rolling summary, recalled
    /// memories. Framed as background reference, never as instructions. Null when nothing to inject.</summary>
    public static string? Ephemeral(string renderedState, string? summary, IReadOnlyList<MemoryHit> hits)
    {
        var sb = new StringBuilder();

        if (renderedState.Length > 0)
            sb.Append("## Operational state (maintained by the harness; authoritative)\n")
              .Append(renderedState);

        if (!string.IsNullOrWhiteSpace(summary))
            sb.Append(sb.Length > 0 ? "\n\n" : "")
              .Append("## Summary of earlier conversation\n")
              .Append("(Background reference only — the messages below are the live conversation.)\n")
              .Append(summary);

        if (hits.Count > 0)
            sb.Append(sb.Length > 0 ? "\n\n" : "")
              .Append("## Relevant memories\n")
              .Append("(Recalled from long-term memory; may be stale — verify before relying on them.)\n")
              .Append(string.Join("\n", hits.Select(h => $"- ({h.Entry.Scope}/{h.Entry.Kind}) {h.Entry.Content}")));

        return sb.Length == 0 ? null : sb.ToString();
    }
}
