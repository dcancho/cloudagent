using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Surfaces;

/// <summary>A message that was accepted and has not been answered yet.</summary>
/// <param name="Id">Identifies the entry within its inbox.</param>
/// <param name="Message">The message as the agent was to read it, with the context overheard before it.</param>
/// <param name="PersistedAt">When it was accepted.</param>
/// <param name="Attempts">How many times it has been redelivered after a restart.</param>
public sealed record InboxEntry(string Id, InboundMessage Message, DateTimeOffset PersistedAt, int Attempts);

/// <summary>
/// A write-ahead log of the messages the agent has accepted, one append-only file per conversation.
/// A message is written before it reaches the agent and struck off once the turn that read it has
/// ended, so whatever a restart interrupted is still here to be redelivered.
/// </summary>
public sealed class InboxStore(string directory, TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    /// <summary>Write a message down and return the id of its entry.</summary>
    public string Add(InboundMessage message)
    {
        var id = Guid.NewGuid().ToString("N");
        Append(message.Surface, message.ConversationId, new Record("add", id, _time.GetUtcNow(), message));
        return id;
    }

    /// <summary>Strike entries off once they are answered or given up on.</summary>
    public void Complete(IEnumerable<string> ids) => Write(ids, "done");

    /// <summary>Count one more redelivery against each entry.</summary>
    public void RecordAttempt(IEnumerable<string> ids) => Write(ids, "attempt");

    /// <summary>Forget everything kept for a conversation.</summary>
    public void Clear(string surface, string conversationId)
    {
        lock (_gate)
        {
            var path = PathFor(surface, conversationId);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>The entries not yet struck off, oldest first, across every conversation.</summary>
    public IReadOnlyList<InboxEntry> Pending()
    {
        lock (_gate)
        {
            if (!Directory.Exists(directory))
                return [];

            return Directory.EnumerateFiles(directory, "*.jsonl").SelectMany(Replay).OrderBy(e => e.PersistedAt).ToList();
        }
    }

    private string PathFor(string surface, string conversationId) =>
        Path.Combine(directory, $"{TranscriptRecorder.ToFileName($"{surface}:{conversationId}")}.jsonl");

    private void Write(IEnumerable<string> ids, string op)
    {
        var wanted = ids.ToHashSet();
        if (wanted.Count == 0)
            return;

        lock (_gate)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").ToList())
            {
                var entries = Replay(file).ToList();
                var hits = entries.Where(e => wanted.Contains(e.Id)).ToList();
                if (hits.Count == 0)
                    continue;

                foreach (var hit in hits)
                    AppendLine(file, new Record(op, hit.Id, _time.GetUtcNow(), null));

                if (op == "done" && entries.All(e => wanted.Contains(e.Id)))
                    File.Delete(file);
            }
        }
    }

    private void Append(string surface, string conversationId, Record record)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            AppendLine(PathFor(surface, conversationId), record);
        }
    }

    private static void AppendLine(string path, Record record)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(record, Json) + "\n");
    }

    private static IEnumerable<InboxEntry> Replay(string path)
    {
        var open = new Dictionary<string, InboxEntry>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }

        foreach (var line in lines.Where(l => l.Length > 0))
        {
            Record? record;
            try
            {
                record = JsonSerializer.Deserialize<Record>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is null)
                continue;

            switch (record.Op)
            {
                case "add" when record.Message is not null:
                    open[record.Id] = new InboxEntry(record.Id, record.Message, record.At, 0);
                    break;
                case "attempt" when open.TryGetValue(record.Id, out var entry):
                    open[record.Id] = entry with { Attempts = entry.Attempts + 1 };
                    break;
                case "done":
                    open.Remove(record.Id);
                    break;
            }
        }

        return open.Values;
    }

    private sealed record Record(string Op, string Id, DateTimeOffset At, InboundMessage? Message);
}
