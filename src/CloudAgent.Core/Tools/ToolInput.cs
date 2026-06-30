using System;
using System.Text.Json.Nodes;

namespace CloudAgent.Core.Tools;

/// <summary>
/// The arguments handed to a tool. Backed by a JSON object so it maps cleanly to what
/// an LLM emits for a tool call, with typed accessors for convenience.
/// </summary>
public sealed class ToolInput
{
    private readonly JsonObject _args;

    public ToolInput(JsonObject args) => _args = args ?? new JsonObject();

    public static ToolInput Empty => new(new JsonObject());

    /// <summary>Parse a JSON object string (as a model would produce) into tool input.</summary>
    public static ToolInput FromJson(string json) =>
        new(JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("Tool arguments must be a JSON object.", nameof(json)));

    public bool Has(string name) => _args.ContainsKey(name);

    /// <summary>Required string; throws if missing or empty.</summary>
    public string GetString(string name)
    {
        var value = _args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Missing required argument '{name}'.");
        return value;
    }

    public string? GetStringOrNull(string name) =>
        _args.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;

    public bool GetBool(string name, bool fallback = false) =>
        _args.TryGetPropertyValue(name, out var node) && node is not null
            ? node.GetValue<bool>()
            : fallback;

    public int GetInt(string name, int fallback = 0) =>
        _args.TryGetPropertyValue(name, out var node) && node is not null
            ? node.GetValue<int>()
            : fallback;
}
