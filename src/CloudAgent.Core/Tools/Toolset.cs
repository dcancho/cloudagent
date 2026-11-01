using CloudAgent.Core.Sandbox;
using CloudAgent.Core.Tools.Code;
using CloudAgent.Core.Tools.Files;
using CloudAgent.Core.Tools.Web;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools;

/// <summary>Convenience builders for the agent's standard toolbox.</summary>
public static class Toolset
{
    /// <summary>
    /// The default set: file/folder interaction scoped to <paramref name="workspace"/>,
    /// plus web search backed by <paramref name="search"/>.
    /// </summary>
    public static ToolRegistry CreateDefault(IWorkspace workspace, IWebSearchProvider search) =>
        new ToolRegistry()
            .Register(new ReadFileTool(workspace))
            .Register(new WriteFileTool(workspace))
            .Register(new ListDirectoryTool(workspace))
            .Register(new CreateDirectoryTool(workspace))
            .Register(new DeletePathTool(workspace))
            .Register(new WebSearchTool(search));

    /// <summary>
    /// The default set plus what working on code needs: exact edits, exact search, and running
    /// the programs <paramref name="workspace"/> allows, in a container when <paramref name="sandbox"/> is given.
    /// </summary>
    public static ToolRegistry CreateCode(IWorkspace workspace, IWebSearchProvider search, SandboxLauncher? sandbox = null) =>
        CreateDefault(workspace, search)
            .Register(new EditFileTool(workspace))
            .Register(new SearchFilesTool(workspace))
            .Register(new RunCommandTool(workspace, sandbox: sandbox));
}
