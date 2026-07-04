using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Shows the consolidation proposals the sleep job has staged — what it wants to learn and
/// the evidence — so a human (or a deliberate agent turn) can review before anything is
/// written to long-term memory.
/// </summary>
public sealed class ListConsolidationsTool(PendingConsolidations pending) : ITool
{
    public string Name => "list_consolidations";

    public string Description =>
        "List pending memory-consolidation proposals staged by the sleep job (distilled facts " +
        "with evidence, plus the episode summary). Use apply_consolidation to accept one.";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var proposals = pending.List();
        if (proposals.Count == 0)
            return Task.FromResult(ToolResult.Ok("No pending consolidation proposals."));

        var sb = new StringBuilder();
        foreach (var proposal in proposals)
        {
            sb.AppendLine($"Proposal {proposal.Id} ({proposal.At:u}, from {proposal.AgentId}):");
            foreach (var fact in proposal.Facts)
                sb.AppendLine($"  - [{fact.Scope}, {fact.Importance:0.00}] {fact.Fact} (evidence: {fact.Evidence})");
            if (proposal.EpisodeSummary is not null)
                sb.AppendLine($"  Summary replacing {proposal.Replaces.Count} episode(s): {proposal.EpisodeSummary}");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}
