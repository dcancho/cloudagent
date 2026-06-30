using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>
/// Answers a child's outstanding question. The parent uses this when it sees a "[question from
/// &lt;id&gt;]" message — it replies by the child's id and the bus delivers it to the waiting child.
/// </summary>
public sealed class ReplyToAgentTool(AgentBus bus, AgentHandle self) : ITool
{
    public string Name => "reply_to_agent";

    public string Description => "Answer a question a child agent asked you, by the child's id.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("agent_id", "The id of the child you're answering."),
        ToolParameter.String("answer", "Your answer."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var agentId = input.GetString("agent_id");
        var delivered = await bus
            .AnswerLatestAsync(self.Id, agentId, input.GetString("answer"))
            .ConfigureAwait(false);

        return delivered
            ? ToolResult.Ok($"Replied to {agentId}.")
            : ToolResult.Fail($"{agentId} has no pending question.");
    }
}
