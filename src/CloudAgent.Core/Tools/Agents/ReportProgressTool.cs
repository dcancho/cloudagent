using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>Lets a child send a non-blocking progress update to its parent.</summary>
public sealed class ReportProgressTool(AgentBus bus, AgentHandle self) : ITool
{
    public string Name => "report_progress";

    public string Description => "Send a short progress update to your parent (does not wait for a reply).";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("update", "What you've done or where you are."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (self.ParentId is null)
            return ToolResult.Fail("You have no parent to update.");

        await bus.SendAsync(
            new AgentMessage(self.Id, self.ParentId, MessageKind.Progress, input.GetString("update")),
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Ok("Progress reported.");
    }
}
