using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>Lists the caller's child agents with their status and result.</summary>
public sealed class ListAgentsTool(AgentBus bus, AgentHandle self) : ITool
{
    public string Name => "list_agents";

    public string Description => "List your child agents with their role, status, and any result.";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var children = bus.ChildrenOf(self.Id).ToArray();
        if (children.Length == 0)
            return Task.FromResult(ToolResult.Ok("You have no child agents."));

        var sb = new StringBuilder();
        foreach (var child in children)
        {
            sb.Append($"- {child.Id} ({child.Role}): {child.Status}");
            if (!string.IsNullOrEmpty(child.Result))
                sb.Append($" — {child.Result}");
            sb.AppendLine();
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}
