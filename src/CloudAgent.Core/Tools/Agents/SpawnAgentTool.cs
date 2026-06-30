using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>Spawns a child agent with a scoped task. Returns immediately; the child runs concurrently.</summary>
public sealed class SpawnAgentTool(AgentOrchestrator orchestrator, AgentHandle self) : ITool
{
    public string Name => "spawn_agent";

    public string Description =>
        "Create a child agent to work on a scoped task concurrently. It has its own memory and can ask you questions. " +
        "Returns the child's id; use it to track or message the child.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("role", "A short role/name for the child (e.g. 'researcher')."),
        ToolParameter.String("task", "The scoped task to assign the child."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var role = input.GetString("role");
        var child = orchestrator.Spawn(role, input.GetString("task"), self.Id);
        return Task.FromResult(ToolResult.Ok($"Spawned {role} as {child.Id}."));
    }
}
