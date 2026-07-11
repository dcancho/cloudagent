using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Attention;

/// <summary>
/// The attention axis: given the working context and this turn's query, decide what content
/// reaches the model — the stable system prefix, the volatile per-turn context, and which
/// memories were injected. Named for the ROLE it plays in the system, not for what its first
/// implementation does: <see cref="ContextAssembler"/> assembles, but an eje-8 metacognitive gate
/// that suppresses low-confidence injections is a different implementation of the same role —
/// both are attention managers, only one is an "assembler".
/// </summary>
public interface IAttentionManager
{
    /// <summary>Assemble the prompt inputs for one turn.</summary>
    /// <param name="context">Working context — read for operational state and rolling summary.</param>
    /// <param name="query">The recall query, typically the latest user turn; null/blank skips recall.</param>
    Task<AssembledContext> AssembleAsync(
        AgentContext context, string? query, CancellationToken cancellationToken = default);
}
