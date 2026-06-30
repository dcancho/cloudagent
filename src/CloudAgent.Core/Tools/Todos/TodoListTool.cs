using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Todos;

namespace CloudAgent.Core.Tools.Todos;

/// <summary>Fetches the current taskboard.</summary>
public sealed class TodoListTool(TodoStore store) : ITool
{
    public string Name => "todo_list";

    public string Description => "List the tasks on the taskboard, with their ids and status.";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (store.Count == 0)
            return Task.FromResult(ToolResult.Ok("The taskboard is empty."));

        var sb = new StringBuilder();
        foreach (var item in store.Items)
        {
            var box = item.Status == TodoStatus.Done ? "[x]" : "[ ]";
            sb.AppendLine($"{box} #{item.Id} {item.Title}");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}
