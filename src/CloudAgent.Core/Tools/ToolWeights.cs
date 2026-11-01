using System;
using System.Collections.Generic;

namespace CloudAgent.Core.Tools;

/// <summary>
/// What each tool call costs against a turn's tool points. A calculation is nearly free, a web search
/// or a page fetch costs several, because it brings a large result into every later step.
/// </summary>
public static class ToolWeights
{
    public const int Default = 2;

    private static readonly Dictionary<string, int> Known = new(StringComparer.Ordinal)
    {
        ["request_tools"] = 0,
        ["calculate"] = 1,
        ["remember"] = 1,
        ["search_memory"] = 1,
        ["read_result"] = 1,
        ["restore_memory"] = 1,
        ["list_consolidations"] = 1,
        ["read_file"] = 1,
        ["list_directory"] = 1,
        ["search_files"] = 1,
        ["list_agents"] = 1,
        ["web_search"] = 3,
        ["fetch_url"] = 3,
        ["read_page"] = 2,
        ["ask_page"] = 2,
        ["summarize_file"] = 3,
        ["index_workspace"] = 4,
        ["run_command"] = 3,
        ["spawn_agent"] = 3,
        ["delegate_task"] = 3,
    };

    /// <summary>The points one call of <paramref name="tool"/> costs.</summary>
    public static int Of(string tool) =>
        Known.TryGetValue(tool, out var weight) ? weight
        : tool.StartsWith("todo_", StringComparison.Ordinal) ? 1
        : Default;
}
