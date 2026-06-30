using System.Text.Json.Nodes;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// A tool advertised to the model: its name, what it does, and a JSON-schema object
/// describing its arguments. Providers translate this into their own tool format.
/// </summary>
/// <param name="Name">Tool name the model calls.</param>
/// <param name="Description">What the tool does.</param>
/// <param name="Schema">JSON-schema object for the arguments (type: object).</param>
public sealed record ToolDefinition(string Name, string Description, JsonObject Schema);
