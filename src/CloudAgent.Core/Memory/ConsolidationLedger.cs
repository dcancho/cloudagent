using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>One consolidation action, as recorded in the ledger.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Action">What kind of action (e.g. "demote", "promote", "stage", "apply").</param>
/// <param name="Detail">Human-readable specifics — memory id, thresholds crossed, proposal id.</param>
/// <param name="Prev">Hash of the previous record (hex), empty for the first.</param>
/// <param name="Hash">SHA-256 over Prev + At + Action + Detail (hex).</param>
public sealed record ConsolidationRecord(
    DateTimeOffset At,
    string Action,
    string Detail,
    string Prev,
    string Hash);

/// <summary>
/// Append-only, hash-chained record of every consolidation decision — the audit trail that
/// makes memory management explainable after the fact. Each record carries the hash of its
/// predecessor, so silent edits or deletions break the chain and are detectable with
/// <see cref="Verify"/>. Single-process by assumption.
/// </summary>
public sealed class ConsolidationLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _path;
    private string _lastHash = "";

    public ConsolidationLedger(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (!File.Exists(path))
            return;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var record = TryParse(line);
            if (record is not null)
                _lastHash = record.Hash;
        }
    }

    public async Task<ConsolidationRecord> AppendAsync(
        string action, string detail, CancellationToken cancellationToken = default)
    {
        var at = DateTimeOffset.UtcNow;
        var record = new ConsolidationRecord(at, action, detail, _lastHash, ComputeHash(_lastHash, at, action, detail));

        await File.AppendAllLinesAsync(
            _path, [JsonSerializer.Serialize(record, JsonOptions)], cancellationToken).ConfigureAwait(false);

        _lastHash = record.Hash;
        return record;
    }

    /// <summary>Walk the whole chain; false if any record was altered, inserted, or removed.</summary>
    public bool Verify()
    {
        if (!File.Exists(_path))
            return true;

        var prev = "";
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = TryParse(line);
            if (record is null || record.Prev != prev
                || record.Hash != ComputeHash(record.Prev, record.At, record.Action, record.Detail))
                return false;

            prev = record.Hash;
        }

        return true;
    }

    private static ConsolidationRecord? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ConsolidationRecord>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ComputeHash(string prev, DateTimeOffset at, string action, string detail)
    {
        var payload = $"{prev}|{at.ToString("O", CultureInfo.InvariantCulture)}|{action}|{detail}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
