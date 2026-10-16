using CloudAgent.Core.Loop;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Agents;

/// <summary>
/// One kind of agent the same loop can be: who it is, where it works, what it can use, and where
/// its project knowledge lives. A coding agent is a profile, not a separate agent, so memory and
/// consolidation stay one machinery whatever the work is.
/// </summary>
/// <param name="Name">Short name, shown to people choosing where an agent works.</param>
/// <param name="Options">Loop options of a root agent with this profile, persona included.</param>
/// <param name="ChildOptions">Loop options of the subagents it delegates to.</param>
/// <param name="Workspace">Where its file tools act, and what it may run there.</param>
/// <param name="Tools">The tools it and its subagents share.</param>
/// <param name="ProjectMemoryPath">The database of what it knows about this workspace.</param>
/// <param name="Routed">Whether context routing may narrow its tools and memory per turn.</param>
public sealed record AgentProfile(
    string Name,
    AgentOptions Options,
    AgentOptions ChildOptions,
    IWorkspace Workspace,
    ToolRegistry Tools,
    string ProjectMemoryPath,
    bool Routed = true);
