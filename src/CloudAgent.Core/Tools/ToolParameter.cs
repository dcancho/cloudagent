namespace CloudAgent.Core.Tools;

/// <summary>
/// One input a tool accepts. The <see cref="Type"/> uses JSON-schema primitive names
/// ("string", "integer", "number", "boolean") so a tool's parameter list can later be
/// emitted as a JSON schema for provider-side tool calling.
/// </summary>
/// <param name="Name">Argument name.</param>
/// <param name="Type">JSON-schema primitive type.</param>
/// <param name="Description">What the argument means.</param>
/// <param name="Required">Whether the caller must supply it.</param>
public sealed record ToolParameter(
    string Name,
    string Type,
    string Description,
    bool Required = true)
{
    public static ToolParameter String(string name, string description, bool required = true) =>
        new(name, "string", description, required);

    public static ToolParameter Integer(string name, string description, bool required = true) =>
        new(name, "integer", description, required);

    public static ToolParameter Boolean(string name, string description, bool required = true) =>
        new(name, "boolean", description, required);
}
