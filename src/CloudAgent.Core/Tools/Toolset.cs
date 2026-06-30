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
}
