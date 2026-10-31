using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Applies (or discards) a staged consolidation proposal. This is the approval gate of the
/// staged-write design: distilled knowledge only enters long-term memory through here, and
/// the action lands in the tamper-evident ledger either way.
/// </summary>
/// <param name="pending">The staged proposals.</param>
/// <param name="agentId">The conversation using the tool; it can act only on the proposals learned from it.</param>
/// <param name="memory">The conversation's memory, where applied facts are written.</param>
/// <param name="ledger">The ledger every apply, discard and rejection is recorded in.</param>
public sealed class ApplyConsolidationTool(
    PendingConsolidations pending,
    string agentId,
    MemoryManager memory,
    ConsolidationLedger ledger) : ITool
{
    public string Name => "apply_consolidation";

    public string Description =>
        "Apply a pending consolidation proposal by id (writes its facts, stores the summary, " +
        "demotes the replaced episodes to cold), or discard it with discard=true. To accept only some " +
        "facts, list their numbers in facts; the others are discarded. Only do this when the user has " +
        "reviewed the proposal or asked you to.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("id", "The proposal id (see list_consolidations)."),
        ToolParameter.Boolean("discard", "Discard instead of apply (default false).", required: false),
        ToolParameter.String("facts", "Numbers of the facts to apply, as listed, for example 1,3. Default: all.", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var id = input.GetString("id");
        var proposal = pending.Get(id);
        if (proposal is null || proposal.AgentId != agentId)
            return ToolResult.Fail($"No pending proposal '{id}'.");

        if (input.GetBool("discard"))
        {
            pending.Remove(id);
            await ledger.AppendAsync("discard", $"proposal {id} discarded", cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok($"Discarded proposal {id}.");
        }

        if (input.GetStringOrNull("facts") is { Length: > 0 } chosen)
        {
            var numbers = chosen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => int.TryParse(n, out var i) ? i : 0)
                .ToHashSet();
            if (numbers.Any(n => n < 1 || n > proposal.Facts.Count))
                return ToolResult.Fail($"Proposal {id} has facts 1 to {proposal.Facts.Count}; got '{chosen}'.");
            var dropped = proposal.Facts.Where((_, i) => !numbers.Contains(i + 1)).ToList();
            proposal = proposal with { Facts = proposal.Facts.Where((_, i) => numbers.Contains(i + 1)).ToList() };
            foreach (var fact in dropped)
                await ledger.AppendAsync("reject", $"proposal {id}: {fact.Fact}", cancellationToken).ConfigureAwait(false);
        }

        var outcome = await pending.ApplyAsync(proposal, memory, ledger, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok($"Applied {outcome}.");
    }
}
