using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>
/// A child declares its task complete: it reports the result to its parent, records it, and
/// stops. After this the child's loop winds down.
/// </summary>
public sealed class FinishTool(AgentBus bus, AgentHandle self) : ITool
{
    public string Name => "finish";

    public string Description => "Finish your task: report your final result to your parent and stop.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("result", "Your final result or summary of what you did."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var result = input.GetString("result");

        if (self.ParentId is not null)
        {
            await bus.SendAsync(new AgentMessage(self.Id, self.ParentId, MessageKind.Result, result),
                cancellationToken).ConfigureAwait(false);
        }

        // Mark done with the result and wind the child's loop down.
        bus.Complete(self, result);
        return ToolResult.Ok("Finished.");
    }
}
