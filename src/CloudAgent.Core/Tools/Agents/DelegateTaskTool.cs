using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>
/// The blocking convenience: spawn a child for a scoped task and wait for its result. Use for
/// independent chunks that don't need a back-channel. The child can still ask questions, which
/// you'll see and answer; this call returns once the child finishes.
/// </summary>
public sealed class DelegateTaskTool(AgentOrchestrator orchestrator, AgentHandle self) : ITool
{
    public string Name => "delegate_task";

    public string Description =>
        "Spawn a child agent for a scoped task and wait for its final result. Best for self-contained work.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("role", "A short role/name for the child."),
        ToolParameter.String("task", "The scoped task to delegate."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var child = orchestrator.Spawn(input.GetString("role"), input.GetString("task"), self.Id);

        try
        {
            var result = await child.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok($"{child.Id} finished: {result}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"{child.Id} was stopped before finishing.");
        }
    }
}
