using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Brings a Cold memory back into recall and returns it in full. The restore lands in the
/// consolidation ledger with the agent's reason, and the memory's utility record starts over, so
/// the sleep job and the metacognitive gate judge it on what happens after it came back.
/// </summary>
public sealed class RestoreMemoryTool(
    MemoryManager memory,
    IUtilityLedger utility,
    ConsolidationLedger ledger,
    string agentId,
    IReadOnlyCollection<MemoryScope> scopes) : ITool
{
    public string Name => "restore_memory";

    public string Description =>
        "Restore a set-aside memory by id (from the set-aside memories in your context, or search_memory " +
        "with archived=true) and read it in full. It returns to normal recall from then on.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("id", "The memory id."),
        ToolParameter.String("reason", "Why it applies to the current work, in one sentence."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var id = input.GetString("id").Trim();
        var reason = input.GetString("reason").Trim();

        var entry = await memory.FindAsync(id, scopes, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            return ToolResult.Fail($"No memory '{id}'.");

        if (entry.Residency != MemoryResidency.Cold)
            return ToolResult.Ok($"Memory {id} is not set aside ({entry.Residency}). Content: {entry.Content}");

        await memory.SetResidencyAsync(entry, MemoryResidency.Active, cancellationToken).ConfigureAwait(false);
        await utility.AppendAsync(
            [new UtilityEvent(id, agentId, 0, DateTimeOffset.UtcNow, UtilitySignal.Restored, reason)],
            cancellationToken).ConfigureAwait(false);
        await ledger.AppendAsync("restore", $"{id} -> Active by {agentId}: {reason}", cancellationToken)
            .ConfigureAwait(false);

        return ToolResult.Ok($"Restored ({entry.Scope}/{entry.Kind}): {entry.Content}");
    }
}
