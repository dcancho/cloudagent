using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Keeps each agent's working context on disk so a conversation can continue after the process
/// restarts: its messages, rolling summary, and operational state. Unlike a transcript, which is an
/// append-only record of everything, a snapshot is overwritten after every turn and holds only what
/// the agent needs to go on, already compacted.
/// </summary>
public sealed class ContextStore(string directory)
{
    public string PathFor(string agentId) => Path.Combine(directory, $"{TranscriptRecorder.ToFileName(agentId)}.json");

    public bool Exists(string agentId) => File.Exists(PathFor(agentId));

    /// <summary>Write the context atomically, so a crash mid-write never leaves a half snapshot.</summary>
    public void Save(string agentId, AgentContext context)
    {
        Directory.CreateDirectory(directory);
        var snapshot = new JsonObject
        {
            ["agent"] = agentId,
            ["savedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["rollingSummary"] = context.RollingSummary,
            ["state"] = context.State.IsEmpty ? null : JsonNode.Parse(context.State.ToJson()),
            ["messages"] = new JsonArray(context.Messages.Select(ToJson).ToArray()),
        };

        var path = PathFor(agentId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, snapshot.ToJsonString(TranscriptRecorder.Readable));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The saved context, or null when there is none or it cannot be read.</summary>
    public AgentContext? Load(string agentId)
    {
        var path = PathFor(agentId);
        if (!File.Exists(path))
            return null;

        try
        {
            var snapshot = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var context = new AgentContext
            {
                RollingSummary = snapshot["rollingSummary"]?.GetValue<string>(),
                State = snapshot["state"] is JsonObject state
                    ? OperationalState.FromJson(state.ToJsonString()) ?? new OperationalState()
                    : new OperationalState(),
            };
            foreach (var node in snapshot["messages"]!.AsArray())
                context.Add(FromJson(node!.AsObject()));
            return context;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException
                                       or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public void Delete(string agentId)
    {
        var path = PathFor(agentId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static JsonNode ToJson(Message message)
    {
        var json = new JsonObject
        {
            ["role"] = message.Role.ToString(),
            ["content"] = message.Content,
        };
        if (message.ToolCalls.Count > 0)
        {
            json["toolCalls"] = new JsonArray(message.ToolCalls
                .Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.Arguments })
                .ToArray());
        }
        if (message.ToolCallId is not null)
            json["toolCallId"] = message.ToolCallId;
        if (message.ToolName is not null)
            json["toolName"] = message.ToolName;
        return json;
    }

    private static Message FromJson(JsonObject json)
    {
        var role = Enum.Parse<Role>(json["role"]!.GetValue<string>());
        var toolCalls = json["toolCalls"] is JsonArray calls
            ? calls.Select(c => new ToolCall(
                c!["id"]!.GetValue<string>(), c["name"]!.GetValue<string>(), c["arguments"]!.GetValue<string>())).ToList()
            : (IReadOnlyList<ToolCall>)[];

        return new Message(role, json["content"]?.GetValue<string>() ?? string.Empty)
        {
            ToolCalls = toolCalls,
            ToolCallId = json["toolCallId"]?.GetValue<string>(),
            ToolName = json["toolName"]?.GetValue<string>(),
        };
    }
}
