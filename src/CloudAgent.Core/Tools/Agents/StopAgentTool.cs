using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>Stops a child agent (and any of its descendants).</summary>
public sealed class StopAgentTool(AgentBus bus) : ITool
{
    public string Name => "stop_agent";

    public string Description => "Stop a child agent by its id (also stops anything it spawned).";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("agent_id", "The id of the agent to stop."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var agentId = input.GetString("agent_id");
        if (!bus.TryGet(agentId, out _))
            return Task.FromResult(ToolResult.Fail($"No agent '{agentId}'."));

        bus.Stop(agentId);
        return Task.FromResult(ToolResult.Ok($"Stopped {agentId}."));
    }
}
