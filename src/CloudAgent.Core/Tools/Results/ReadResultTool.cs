using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools.Results;

/// <summary>Reads more of a tool result that was too long to go into the conversation whole.</summary>
public sealed class ReadResultTool(ToolResultStore store) : ITool
{
    public const string ToolName = "read_result";

    private const int DefaultLength = 3000;
    private const int MaxLength = 8000;

    public string Name => ToolName;

    public string Description =>
        "Read more of an earlier tool result that was cut short. Give the result id shown at the end of it " +
        "and the offset to continue from.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("id", "The result id shown in the cut result."),
        ToolParameter.Integer("offset", "Character to start from (default 0).", required: false),
        ToolParameter.Integer("length", $"Characters to read (default {DefaultLength}, at most {MaxLength}).", required: false),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var id = input.GetString("id");
        var requested = input.GetInt("offset", 0);
        var length = System.Math.Clamp(input.GetInt("length", DefaultLength), 1, MaxLength);

        var total = store.LengthOf(id);
        var offset = total is { } size ? System.Math.Clamp(requested, 0, size) : 0;
        var text = store.Read(id, offset, length);
        if (total is null || text is null)
            return Task.FromResult(ToolResult.Fail($"No stored result '{id}'."));

        var end = System.Math.Min(offset + text.Length, total.Value);
        var note = end < total
            ? $"\n[characters {offset} to {end} of {total}; continue with offset {end}]"
            : $"\n[characters {offset} to {end} of {total}; end of result]";
        return Task.FromResult(ToolResult.Ok(text + note));
    }
}
