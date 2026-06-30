using System.Threading.Tasks;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Agents;

namespace CloudAgent.Core.Agents;

/// <summary>
/// Builds the loop for a freshly registered child: the host supplies provider, options, its
/// own fresh context, and its own memory (children do not share the parent's memory). The
/// orchestrator hands it the composed tool set and the mailbox input.
/// </summary>
public delegate AgentLoop SubagentLoopBuilder(AgentHandle child, IInputSource input, ToolRegistry tools);

/// <summary>
/// Spawns and runs child agents. Each child is a real <see cref="AgentLoop"/> running as its own
/// task, fed by its mailbox, with a scoped task delivered as its first message. Children share
/// the workspace tools they're given but get their own context and memory.
/// </summary>
public sealed class AgentOrchestrator(AgentBus bus, ToolRegistry sharedTools, SubagentLoopBuilder build)
{
    /// <summary>Spawn a child under <paramref name="parentId"/> with a scoped task and start it.</summary>
    public AgentHandle Spawn(string role, string task, string? parentId)
    {
        var child = bus.Register(role, parentId);
        var input = bus.InputFor(child);
        var tools = ComposeChildTools(child);
        var loop = build(child, input, tools);

        child.Runner = Task.Run(() => RunAsync(child, loop, task), child.Token);
        return child;
    }

    private ToolRegistry ComposeChildTools(AgentHandle child)
    {
        var registry = new ToolRegistry();
        foreach (var tool in sharedTools.All)
            registry.Register(tool);

        registry
            .Register(new AskParentTool(bus, child))
            .Register(new ReportProgressTool(bus, child))
            .Register(new FinishTool(bus, child));

        return registry;
    }

    private async Task RunAsync(AgentHandle child, AgentLoop loop, string task)
    {
        bus.SetStatus(child, AgentStatus.Running);

        // Deliver the scoped task as the child's first stimulus.
        await bus.SendAsync(new AgentMessage(child.ParentId ?? "system", child.Id, MessageKind.Task, task))
            .ConfigureAwait(false);

        try
        {
            await loop.RunAsync(child.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // stopped — expected
        }

        if (child.Status != AgentStatus.Done)
            bus.SetStatus(child, AgentStatus.Stopped);
    }
}
