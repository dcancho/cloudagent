using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Surfaces;
using CloudAgent.Core.Tools;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Writes everything that happens in one agent's loop to an append-only JSONL file: what came in,
/// what the model answered and asked for, what the tools returned, how the turn was routed, what was
/// recalled and judged, and what failed. One line per event, so a conversation can be replayed and
/// measured after the process is gone.
/// </summary>
public sealed class TranscriptRecorder
{
    /// <summary>JSON written for people to read: accents and symbols stay as they are, not as escapes.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Readable =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _path;
    private readonly int _maxChars;
    private readonly object _gate = new();
    private long? _turnStarted;

    /// <param name="directory">Where transcripts are kept.</param>
    /// <param name="agentId">The agent whose loop is recorded; also the file name.</param>
    /// <param name="maxChars">Longer texts, typically tool results, are cut to this length.</param>
    public TranscriptRecorder(string directory, string agentId, int maxChars = 20_000)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"{ToFileName(agentId)}.jsonl");
        _maxChars = maxChars;
    }

    public string FilePath => _path;

    /// <summary>
    /// A file-system-safe form of an agent id. Ids look like <c>webchat:1a2b3c4d</c>, and a colon in a
    /// Windows file name silently writes to an NTFS alternate data stream instead of a file.
    /// </summary>
    public static string ToFileName(string agentId)
    {
        var invalid = Path.GetInvalidFileNameChars().Append(':').ToHashSet();
        return new string(agentId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>
    /// The conversation as people saw it, read from an agent's transcript: what the person sent, what
    /// the agent answered, and what each of its tool calls did, in order. Other events are left out.
    /// </summary>
    public static IReadOnlyList<ConversationEntry> ReadConversation(string directory, string agentId)
    {
        var path = Path.Combine(directory, $"{ToFileName(agentId)}.jsonl");
        if (!File.Exists(path))
            return [];

        var entries = new List<ConversationEntry>();
        var calls = new Dictionary<string, ToolCall>();
        foreach (var line in File.ReadLines(path))
        {
            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (entry is null)
                continue;

            var kind = entry["kind"]?.GetValue<string>();
            if (kind == "tool")
            {
                var id = entry["id"]?.GetValue<string>();
                if (id is not null && calls.Remove(id, out var call))
                {
                    var result = entry["success"]?.GetValue<bool>() == true
                        ? ToolResult.Ok(entry["content"]?.GetValue<string>() ?? "")
                        : ToolResult.Fail(entry["error"]?.GetValue<string>() ?? "failed");
                    entries.Add(new ConversationEntry("activity", "", ToolActivity.From(call, result)));
                }
                continue;
            }
            if (kind == "assistant" && entry["toolCalls"] is JsonArray requested)
            {
                foreach (var c in requested.OfType<JsonObject>())
                {
                    var call = new ToolCall(
                        c["id"]?.GetValue<string>() ?? "", c["name"]?.GetValue<string>() ?? "", c["arguments"]?.GetValue<string>() ?? "{}");
                    calls[call.Id] = call;
                }
            }

            var text = entry["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text) || kind is not ("user" or "assistant"))
                continue;
            entries.Add(new ConversationEntry(kind, text));
        }
        return entries;
    }

    /// <summary>
    /// The conversations recorded for one surface, most recently changed first. Each is titled by
    /// the first thing the person said; transcripts where they never said anything are left out.
    /// </summary>
    public static IReadOnlyList<ConversationSummary> ListConversations(
        string directory, string surface, int titleLength = 60)
    {
        if (!Directory.Exists(directory))
            return [];

        var prefix = ToFileName($"{surface}:");
        var conversations = new List<ConversationSummary>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles($"{prefix}*.jsonl"))
        {
            var first = FirstUserText(file.FullName);
            if (first is null)
                continue;

            var id = Path.GetFileNameWithoutExtension(file.Name)[prefix.Length..];
            conversations.Add(new ConversationSummary(id, Title(first, titleLength), file.LastWriteTimeUtc));
        }
        return conversations.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    private static string? FirstUserText(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"kind\":\"user\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    var text = JsonNode.Parse(line)?["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    private static string Title(string text, int length)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= length)
            return flat;

        var cut = flat.LastIndexOf(' ', length);
        return (cut > length / 2 ? flat[..cut] : flat[..length]).TrimEnd(',', '.', ';', ':') + "...";
    }

    public void Attach(AgentLoop loop)
    {
        loop.InputReceived += text =>
        {
            _turnStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Write("user", new JsonObject { ["text"] = Cut(text) });
        };

        loop.Routed += d => Write("routing", new JsonObject
        {
            ["memory"] = d.Memory.ToString(),
            ["categories"] = Array(d.Categories.Order()),
            ["proposedMemory"] = d.ProposedMemory.ToString(),
            ["proposedCategories"] = Array(d.ProposedCategories.Order()),
            ["guidance"] = Array(d.Guidance),
            ["shadow"] = d.Shadow,
            ["fellBack"] = d.FellBack,
            ["elapsedMs"] = (int)d.Elapsed.TotalMilliseconds,
        });

        loop.MemoriesRecalled += count => Write("recall", new JsonObject { ["count"] = count });

        loop.ResponseReceived += (iteration, response) =>
        {
            var entry = new JsonObject
            {
                ["iteration"] = iteration,
                ["text"] = Cut(response.Text),
                ["stop"] = response.StopReason.ToString(),
                ["inputTokens"] = response.InputTokens,
                ["outputTokens"] = response.OutputTokens,
                ["toolCalls"] = new JsonArray(response.ToolCalls
                    .Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = Cut(c.Arguments) })
                    .ToArray()),
            };
            if (_turnStarted is { } started)
                entry["sinceInputMs"] = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Write("assistant", entry);
        };

        loop.ToolInvoked += (call, result) => Write("tool", new JsonObject
        {
            ["id"] = call.Id,
            ["name"] = call.Name,
            ["success"] = result.Success,
            ["content"] = result.Success ? Cut(result.Content) : null,
            ["error"] = result.Error,
        });

        loop.UtilityJudged += (useful, injected) =>
            Write("judge", new JsonObject { ["useful"] = useful, ["injected"] = injected });

        loop.Compacted += evicted => Write("compaction", new JsonObject { ["evicted"] = evicted });

        loop.TurnFailed += (iteration, ex) => Write("error", new JsonObject
        {
            ["iteration"] = iteration,
            ["type"] = ex.GetType().Name,
            ["message"] = Cut(ex.Message),
        });
    }

    private void Write(string kind, JsonObject entry)
    {
        var line = new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["kind"] = kind };
        foreach (var (key, value) in entry.ToList())
        {
            entry.Remove(key);
            line[key] = value;
        }

        try
        {
            lock (_gate)
                File.AppendAllText(_path, line.ToJsonString(Readable) + Environment.NewLine);
        }
        catch (IOException)
        {
            // The transcript is a record, not part of the turn; a write failure must not break it.
        }
    }

    private string Cut(string text) =>
        text.Length <= _maxChars ? text : text[.._maxChars] + $"... [{text.Length - _maxChars} more characters]";

    private static JsonArray Array(IEnumerable<string> items) =>
        new(items.Select(i => (JsonNode)i).ToArray());
}
