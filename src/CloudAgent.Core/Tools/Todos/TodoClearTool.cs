using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Todos;

namespace CloudAgent.Core.Tools.Todos;

/// <summary>Clears the whole taskboard.</summary>
public sealed class TodoClearTool(TodoStore store) : ITool
{
    public string Name => "todo_clear";

    public string Description => "Remove all tasks from the taskboard.";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var removed = store.Clear();
        return Task.FromResult(ToolResult.Ok($"Cleared {removed} task(s)."));
    }
}
