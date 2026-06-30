using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools;

/// <summary>
/// A capability the agent can invoke to affect or observe its world. Tools are the verbs
/// of the agent: read a file, list a folder, search the web. The loop (or a provider's
/// tool-calling) selects a tool by <see cref="Name"/> and hands it <see cref="ToolInput"/>.
/// </summary>
public interface ITool
{
    /// <summary>Stable identifier the model uses to call the tool (e.g. "read_file").</summary>
    string Name { get; }

    /// <summary>One-line description of what the tool does, surfaced to the model.</summary>
    string Description { get; }

    /// <summary>The arguments the tool accepts; also the basis for a generated JSON schema.</summary>
    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>Run the tool. Implementations should return <see cref="ToolResult.Fail"/>
    /// for expected errors rather than throwing.</summary>
    Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default);
}
