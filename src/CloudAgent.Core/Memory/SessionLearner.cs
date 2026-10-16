using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Learns from a conversation once it goes quiet. Compaction only learns from turns it evicts, so a
/// conversation that never outgrows its window would leave nothing behind but its transcript. When
/// the conversation has been idle long enough, what the person and the agent said since the last
/// pass is archived as episodes, and the durable facts in it are staged as a proposal for review,
/// never written directly. Tool output is left out: conventions and decisions live in what was said.
/// </summary>
public sealed class SessionLearner(
    MemoryManager memory,
    Consolidator consolidator,
    PendingConsolidations pending,
    ConsolidationLedger ledger,
    string agentId,
    Func<IReadOnlyList<Message>> messages,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HashSet<Message> _learned = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastActivity = (time ?? TimeProvider.System).GetUtcNow();
    private bool _busy;

    /// <summary>Call when the conversation receives a message or finishes a turn.</summary>
    public void Touch(bool busy)
    {
        _lastActivity = _time.GetUtcNow();
        _busy = busy;
    }

    /// <summary>Whether the conversation has been idle at least <paramref name="idle"/> with something new to learn.</summary>
    public bool IsDue(TimeSpan idle) =>
        !_busy && _time.GetUtcNow() - _lastActivity >= idle && Unlearned().Count > 0;

    /// <summary>Archive and stage what is new; returns the staged proposal's id, if one was staged.</summary>
    public async Task<string?> LearnAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = Unlearned();
            if (batch.Count == 0)
                return null;

            var said = batch.Where(m => m.Role is Role.User or Role.Assistant && !string.IsNullOrWhiteSpace(m.Content)).ToList();
            await memory.ArchiveAsync(said, cancellationToken).ConfigureAwait(false);
            var facts = await consolidator
                .ProposeAsync(said, $"conversation {agentId}, {_time.GetUtcNow():yyyy-MM-dd HH:mm} UTC", cancellationToken)
                .ConfigureAwait(false);
            foreach (var message in batch)
                _learned.Add(message);

            if (facts.Count == 0)
            {
                await ledger.AppendAsync("learn", $"{said.Count} message(s) archived, no facts proposed, agent {agentId}", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            var proposal = new ConsolidationProposal(
                Guid.NewGuid().ToString("N")[..12], _time.GetUtcNow(), agentId, facts, null, []);
            await pending.StageAsync(proposal, cancellationToken).ConfigureAwait(false);
            await ledger.AppendAsync(
                "stage", $"proposal {proposal.Id}: {facts.Count} fact(s) from a quiet conversation, agent {agentId}",
                cancellationToken).ConfigureAwait(false);
            return proposal.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<Message> Unlearned()
    {
        try
        {
            return messages().Where(m => !_learned.Contains(m)).ToList();
        }
        catch (InvalidOperationException)
        {
            return []; // a turn started while reading; the next pass picks it up
        }
    }
}
