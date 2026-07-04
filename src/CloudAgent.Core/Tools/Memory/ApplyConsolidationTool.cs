using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Applies (or discards) a staged consolidation proposal. This is the approval gate of the
/// staged-write design: distilled knowledge only enters long-term memory through here, and
/// the action lands in the tamper-evident ledger either way.
/// </summary>
public sealed class ApplyConsolidationTool(
    PendingConsolidations pending,
    MemoryManager memory,
    ConsolidationLedger ledger) : ITool
{
    public string Name => "apply_consolidation";

    public string Description =>
        "Apply a pending consolidation proposal by id (writes its facts, stores the summary, " +
        "demotes the replaced episodes to cold), or discard it with discard=true. Only do this " +
        "when the user has reviewed the proposal or asked you to.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("id", "The proposal id (see list_consolidations)."),
        ToolParameter.Boolean("discard", "Discard instead of apply (default false).", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var id = input.GetString("id");
        var proposal = pending.Get(id);
        if (proposal is null)
            return ToolResult.Fail($"No pending proposal '{id}'.");

        if (input.GetBool("discard"))
        {
            pending.Remove(id);
            await ledger.AppendAsync("discard", $"proposal {id} discarded", cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok($"Discarded proposal {id}.");
        }

        var outcome = await pending.ApplyAsync(proposal, memory, ledger, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok($"Applied {outcome}.");
    }
}
