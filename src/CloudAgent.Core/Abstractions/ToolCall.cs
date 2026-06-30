namespace CloudAgent.Core.Abstractions;

/// <summary>
/// A request from the model to run a tool. <see cref="Arguments"/> is a JSON object
/// string (what every provider emits), parsed by the tool layer at invocation time.
/// </summary>
/// <param name="Id">Provider-assigned id used to correlate the result back.</param>
/// <param name="Name">The tool to invoke.</param>
/// <param name="Arguments">JSON object string of arguments.</param>
public sealed record ToolCall(string Id, string Name, string Arguments);
