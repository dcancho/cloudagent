using System.Collections.Generic;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Attention;

/// <summary>
/// The per-turn output of the attention axis: exactly what reaches the model this turn.
/// </summary>
/// <param name="System">The stable system prefix (persona + resident memories) — cacheable.</param>
/// <param name="EphemeralContext">The volatile per-turn context (operational state, rolling
/// summary, recalled memories), or null when there is nothing volatile to inject. Providers
/// deliver it at the message tail so it does not invalidate the cached prefix.</param>
/// <param name="InjectedMemories">The memories that were injected, surfaced for the feedback loop
/// (utility recording + post-turn judging) and for a future metacognitive gate to reason over.</param>
public sealed record AssembledContext(
    string System,
    string? EphemeralContext,
    IReadOnlyList<MemoryHit> InjectedMemories);
