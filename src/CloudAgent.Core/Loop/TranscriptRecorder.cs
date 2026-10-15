using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Writes everything that happens in one agent's loop to an append-only JSONL file: what came in,
/// what the model answered and asked for, what the tools returned, how the turn was routed, what was
/// recalled and judged, and what failed. One line per event, so a conversation can be replayed and
/// measured after the process is gone.
/// </summary>
public sealed class TranscriptRecorder
{
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
                File.AppendAllText(_path, line.ToJsonString() + Environment.NewLine);
        }
        catch (IOException)
        {
            // The transcript is a record, not part of the turn; a write failure must not break it.
        }
    }

    private string Cut(string text) =>
        text.Length <= _maxChars ? text : text[.._maxChars] + $"... [{text.Length - _maxChars} more characters]";

    private static JsonArray Array(System.Collections.Generic.IEnumerable<string> items) =>
        new(items.Select(i => (JsonNode)i).ToArray());
}
