using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Todos;

namespace CloudAgent.Core.Tools.Todos;

/// <summary>Adds a task to the taskboard.</summary>
public sealed class TodoAddTool(TodoStore store) : ITool
{
    public string Name => "todo_add";

    public string Description => "Add a task to the taskboard.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("title", "What the task is."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var title = input.GetString("title");
        var item = store.Add(title);
        return Task.FromResult(ToolResult.Ok($"Added #{item.Id}: {item.Title}"));
    }
}
