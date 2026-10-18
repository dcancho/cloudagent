using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Attention;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tools.Memory;

/// <summary>
/// Deliberate memory search, beyond what automatic recall injects. Set-aside (Cold) memories come
/// back as previews with their ids, so reading one in full goes through <see cref="RestoreMemoryTool"/>.
/// </summary>
public sealed class SearchMemoryTool(
    MemoryManager memory,
    IReadOnlyCollection<MemoryScope> scopes,
    int previewChars = 80) : ITool
{
    private const int MaxResults = 8;

    public string Name => "search_memory";

    public string Description =>
        "Search long-term memory. With archived=true, search only set-aside memories; they come back " +
        "as previews with ids to pass to restore_memory.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("query", "What to look for."),
        ToolParameter.Boolean("archived", "Search set-aside memories instead of active ones (default false).", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var query = input.GetString("query");
        var archived = input.GetBool("archived");

        var ranked = await memory
            .RecallAsync(query, scopes, MaxResults * 4, cancellationToken, includeCold: archived)
            .ConfigureAwait(false);
        var hits = ranked
            .Where(hit => (hit.Entry.Residency == MemoryResidency.Cold) == archived)
            .Take(MaxResults)
            .ToArray();

        if (hits.Length == 0)
            return ToolResult.Ok(archived ? "No set-aside memories match." : "No memories match.");

        var lines = hits.Select(hit => archived
            ? $"- [{hit.Entry.Id}] ({hit.Entry.Scope}/{hit.Entry.Kind}) {AttentionRendering.Preview(hit.Entry.Content, previewChars)}"
            : $"- ({hit.Entry.Scope}/{hit.Entry.Kind}) {hit.Entry.Content}");
        return ToolResult.Ok(string.Join("\n", lines));
    }
}
