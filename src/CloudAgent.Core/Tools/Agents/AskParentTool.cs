using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Agents;

namespace CloudAgent.Core.Tools.Agents;

/// <summary>
/// Lets a child ask its parent a question and wait for the answer. The call blocks this turn,
/// but the parent is a live actor servicing its mailbox, so it can answer without deadlock.
/// </summary>
public sealed class AskParentTool(AgentBus bus, AgentHandle self) : ITool
{
    public string Name => "ask_parent";

    public string Description =>
        "Ask your parent agent a question and wait for their answer. Use when you need a decision or clarification.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("question", "The question to ask your parent."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (self.ParentId is null)
            return ToolResult.Fail("You have no parent to ask.");

        bus.SetStatus(self, AgentStatus.WaitingForReply);
        try
        {
            var answer = await bus
                .AskAsync(self, self.ParentId, input.GetString("question"), self.Token)
                .ConfigureAwait(false);
            return ToolResult.Ok(answer);
        }
        finally
        {
            bus.SetStatus(self, AgentStatus.Running);
        }
    }
}
