using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Lets the agent deliberately commit a durable fact to memory. Explicit by design — the
/// agent decides what's worth keeping and which scope it belongs to.
/// </summary>
public sealed class RememberTool(MemoryManager memory) : ITool
{
    public string Name => "remember";

    public string Description =>
        "Save a durable fact to memory. scope: 'personal' (about the user / your working style), " +
        "'project' (about this workspace), or 'session' (this run). Use for things worth recalling later.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("content", "The fact to remember, as a self-contained statement."),
        ToolParameter.String("scope", "personal | project | session (default project).", required: false),
        ToolParameter.Integer("importance", "How important, 0-100 (default 50).", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var content = input.GetString("content");

        var scopeText = input.GetStringOrNull("scope") ?? "project";
        if (!Enum.TryParse<MemoryScope>(scopeText, ignoreCase: true, out var scope))
            return ToolResult.Fail($"Unknown scope '{scopeText}'. Use personal, project, or session.");
        if (!memory.Holds(scope))
            return ToolResult.Fail($"This conversation keeps no {scopeText.ToLowerInvariant()} memory.");

        var importance = Math.Clamp(input.GetInt("importance", 50), 0, 100) / 100.0;

        var entry = await memory
            .RememberAsync(content, scope, importance, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ToolResult.Ok($"Remembered ({scope}, importance {importance:0.00}): {entry.Content}");
    }
}
