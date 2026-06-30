using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Todos;

namespace CloudAgent.Core.Tools.Todos;

/// <summary>Removes a task from the taskboard.</summary>
public sealed class TodoRemoveTool(TodoStore store) : ITool
{
    public string Name => "todo_remove";

    public string Description => "Remove a task from the taskboard, by its id.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.Integer("id", "The id of the task to remove."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (!input.Has("id"))
            return Task.FromResult(ToolResult.Fail("An 'id' is required."));

        var id = input.GetInt("id");
        var item = store.Remove(id);
        return Task.FromResult(item is null
            ? ToolResult.Fail($"No task with id #{id}.")
            : ToolResult.Ok($"Removed #{item.Id}: {item.Title}"));
    }
}
