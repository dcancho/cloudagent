using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>
/// JSONL-backed utility ledger: one event per line, append-only, shared by every agent in the
/// harness (memories are shared, so their track record should be too). Existing events are
/// replayed into stats on load. Single-process by assumption — no locking.
/// </summary>
public sealed class FileUtilityLedger : IUtilityLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<string, MemoryUtilityStats> _stats = [];

    public FileUtilityLedger(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Count = 0;
        if (!File.Exists(path))
            return;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            UtilityEvent? utilityEvent;
            try
            {
                utilityEvent = JsonSerializer.Deserialize<UtilityEvent>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a corrupt line loses one event, not the ledger
            }

            if (utilityEvent is not null)
                Fold(utilityEvent);
        }
    }

    public IReadOnlyDictionary<string, MemoryUtilityStats> Stats => _stats;

    public int Count { get; private set; }

    public async Task AppendAsync(
        IReadOnlyList<UtilityEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
            return;

        var lines = events.Select(e => JsonSerializer.Serialize(e, JsonOptions));
        await File.AppendAllLinesAsync(_path, lines, cancellationToken).ConfigureAwait(false);

        foreach (var utilityEvent in events)
            Fold(utilityEvent);
    }

    private void Fold(UtilityEvent utilityEvent)
    {
        if (!_stats.TryGetValue(utilityEvent.MemoryId, out var stats))
            _stats[utilityEvent.MemoryId] = stats = new MemoryUtilityStats();

        stats.Apply(utilityEvent);
        Count++;
    }
}
