using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Tools;

/// <summary>
/// The agent's toolbox: the set of tools available this run, dispatched by name. This is
/// what the loop will consult once tool calling is wired into the provider round-trip.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
        return this;
    }

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>Describe every registered tool as a provider-facing <see cref="ToolDefinition"/>.</summary>
    public IReadOnlyList<ToolDefinition> GetDefinitions()
    {
        var definitions = new List<ToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values)
            definitions.Add(new ToolDefinition(tool.Name, tool.Description, BuildSchema(tool)));
        return definitions;
    }

    /// <summary>Turn a tool's declared parameters into a JSON-schema object.</summary>
    private static JsonObject BuildSchema(ITool tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in tool.Parameters)
        {
            properties[p.Name] = new JsonObject
            {
                ["type"] = p.Type,
                ["description"] = p.Description,
            };
            if (p.Required)
                required.Add(p.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>Invoke a tool by name, returning a failure result if it isn't registered.</summary>
    public async Task<ToolResult> InvokeAsync(
        string name, ToolInput input, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return ToolResult.Fail($"Unknown tool '{name}'.");

        try
        {
            return await tool.InvokeAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Tool '{name}' threw: {ex.Message}");
        }
    }
}
