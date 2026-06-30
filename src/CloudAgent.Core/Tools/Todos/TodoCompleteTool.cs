using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Todos;

namespace CloudAgent.Core.Tools.Todos;

/// <summary>Marks a task as done.</summary>
public sealed class TodoCompleteTool(TodoStore store) : ITool
{
    public string Name => "todo_complete";

    public string Description => "Mark a task on the taskboard as done, by its id.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.Integer("id", "The id of the task to complete."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (!input.Has("id"))
            return Task.FromResult(ToolResult.Fail("An 'id' is required."));

        var id = input.GetInt("id");
        var item = store.Complete(id);
        return Task.FromResult(item is null
            ? ToolResult.Fail($"No task with id #{id}.")
            : ToolResult.Ok($"Completed #{item.Id}: {item.Title}"));
    }
}
