using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Tools;

namespace CloudAgent.Core.Loop;

/// <summary>
/// The heart of the harness: a perceive → think → act cycle that runs until cancelled.
///
/// Each iteration it pulls any waiting input, then enters a think/act sub-loop: it sends
/// the working context (and the available tools) to the LLM, and while the model asks to
/// use tools, it runs them, feeds the results back, and asks again — until the model
/// produces a plain answer. Everything is folded back into the context as it goes.
/// </summary>
public sealed class AgentLoop
{
    /// <summary>Safety bound on tool calls within a single iteration, to avoid runaway loops.</summary>
    private const int MaxToolHopsPerIteration = 10;

    private readonly ILlmProvider _provider;
    private readonly AgentOptions _options;
    private readonly IInputSource _input;
    private readonly AgentContext _context;
    private readonly ToolRegistry? _tools;
    private readonly ContextCompactor? _compactor;
    private readonly Memory.MemoryManager? _memory;
    private readonly Memory.UtilityTracker? _utility;

    /// <summary>Scopes consulted on automatic recall each turn.</summary>
    private static readonly Memory.MemoryScope[] RecallScopes =
        [Memory.MemoryScope.Personal, Memory.MemoryScope.Project, Memory.MemoryScope.Session];

    /// <summary>Formatted memories recalled for the current turn, injected into the system prompt.</summary>
    private string? _recalledForTurn;

    /// <summary>The hits behind <see cref="_recalledForTurn"/>, kept for post-turn utility judging.</summary>
    private IReadOnlyList<Memory.MemoryHit> _recalledHitsForTurn = [];

    public AgentLoop(
        ILlmProvider provider,
        AgentOptions? options = null,
        IInputSource? input = null,
        AgentContext? context = null,
        ToolRegistry? tools = null,
        ContextCompactor? compactor = null,
        Memory.MemoryManager? memory = null,
        Memory.UtilityTracker? utility = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new AgentOptions();
        _input = input ?? new NullInputSource();
        _context = context ?? new AgentContext();
        _tools = tools;
        _compactor = compactor;
        _memory = memory;
        _utility = utility;
    }

    public AgentContext Context => _context;

    /// <summary>Raised when external input is taken in at the start of an iteration.</summary>
    public event Action<string>? InputReceived;

    /// <summary>Raised for each model completion (may fire several times per iteration when tools are used).</summary>
    public event Action<int, LlmResponse>? ResponseReceived;

    /// <summary>Raised after each tool runs, with the call and its result.</summary>
    public event Action<ToolCall, ToolResult>? ToolInvoked;

    /// <summary>Raised when working memory was compacted, with the number of turns evicted.</summary>
    public event Action<int>? Compacted;

    /// <summary>Raised when durable facts were consolidated during compaction, with the count.</summary>
    public event Action<int>? Consolidated;

    /// <summary>Raised when memories were recalled and injected, with the count.</summary>
    public event Action<int>? MemoriesRecalled;

    /// <summary>Raised after the post-turn utility judgment, with (useful, injected) counts.</summary>
    public event Action<int, int>? UtilityJudged;

    /// <summary>
    /// Run the loop until <paramref name="cancellationToken"/> trips or
    /// <see cref="AgentOptions.MaxIterations"/> is reached.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var iteration = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_options.MaxIterations > 0 && iteration >= _options.MaxIterations)
                break;

            iteration++;

            // 1. Perceive: take in anything waiting from the outside world.
            var stimulus = await _input.NextAsync(cancellationToken).ConfigureAwait(false);
            if (stimulus is not null)
            {
                _context.AddUser(stimulus);
                InputReceived?.Invoke(stimulus);
            }

            // The input step may have signalled a stop (e.g. the user typed "exit").
            if (cancellationToken.IsCancellationRequested)
                break;

            // Nothing to act on yet and no standing context — wait for a first stimulus.
            if (_context.IsEmpty)
            {
                await DelayAsync(cancellationToken).ConfigureAwait(false);
                iteration--; // an empty idle tick doesn't count against the budget
                continue;
            }

            // 2. Recall: pull relevant memories for this turn and inject them.
            _recalledForTurn = await RecallAsync(iteration, cancellationToken).ConfigureAwait(false);

            // 3. Think and act until the model produces a plain answer.
            try
            {
                await ThinkAndActAsync(iteration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 3.5. Close the feedback loop: did the injected memories influence the reply?
            await JudgeUtilityAsync(iteration, cancellationToken).ConfigureAwait(false);

            // 4. Keep working memory under budget; evicted turns become episodic memory.
            await CompactAsync(cancellationToken).ConfigureAwait(false);

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ThinkAndActAsync(int iteration, CancellationToken cancellationToken)
    {
        var toolDefinitions = _tools?.GetDefinitions() ?? [];

        for (var hop = 0; ; hop++)
        {
            var request = new LlmRequest
            {
                System = ComposeSystem(),
                Messages = _context.Messages,
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                Temperature = _options.Temperature,
                Tools = toolDefinitions,
            };

            var response = await _provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            // Record the assistant turn (text plus any tool calls it requested).
            _context.Add(Message.Assistant(response.Text, response.ToolCalls));
            ResponseReceived?.Invoke(iteration, response);

            // No tools requested (or none available) — the turn is done.
            if (!response.HasToolCalls || _tools is null)
                return;

            if (hop >= MaxToolHopsPerIteration)
            {
                _context.AddUser(
                    $"[harness] Tool-call limit ({MaxToolHopsPerIteration}) reached; stopping this turn.");
                return;
            }

            // Run each requested tool and feed its result back as a tool message.
            foreach (var call in response.ToolCalls)
            {
                var result = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);
                var content = result.Success ? result.Content : $"ERROR: {result.Error}";
                content = JsonTruncator.Truncate(content, _options.MaxToolResultChars);
                _context.Add(Message.ToolResult(call.Id, call.Name, content));
                ToolInvoked?.Invoke(call, result);
            }
        }
    }

    /// <summary>System prompt plus operational state, rolling summary, and recalled memories.
    /// Summary and memories are framed as background reference, never as active instructions —
    /// the live conversation is always the messages themselves.</summary>
    private string ComposeSystem()
    {
        var sb = new StringBuilder(_options.SystemPrompt);

        var state = _context.State.Render();
        if (state.Length > 0)
            sb.Append("\n\n## Operational state (maintained by the harness; authoritative)\n")
              .Append(state);

        if (!string.IsNullOrWhiteSpace(_context.RollingSummary))
            sb.Append("\n\n## Summary of earlier conversation\n")
              .Append("(Background reference only — the messages below are the live conversation.)\n")
              .Append(_context.RollingSummary);

        if (!string.IsNullOrWhiteSpace(_recalledForTurn))
            sb.Append("\n\n## Relevant memories\n")
              .Append("(Recalled from long-term memory; may be stale — verify before relying on them.)\n")
              .Append(_recalledForTurn);

        return sb.ToString();
    }

    /// <summary>
    /// Recall memories relevant to the latest turn: always the top-K, plus any others scoring
    /// at or above the recall threshold (capped). Returns a formatted block, or null if none.
    /// </summary>
    private async Task<string?> RecallAsync(int iteration, CancellationToken cancellationToken)
    {
        _recalledHitsForTurn = [];
        if (_memory is null)
            return null;

        var query = LatestUserText();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        const int maxCandidates = 10;
        var candidates = await _memory
            .RecallAsync(query, RecallScopes, maxCandidates, cancellationToken)
            .ConfigureAwait(false);

        var selected = new List<Memory.MemoryHit>();
        for (var i = 0; i < candidates.Count; i++)
        {
            // Always include the top-K; beyond that, keep going only while above threshold.
            if (i < _options.RecallTopK || candidates[i].Score >= _options.RecallThreshold)
                selected.Add(candidates[i]);
            else
                break;
        }

        if (selected.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var hit in selected)
            sb.AppendLine($"- ({hit.Entry.Scope}/{hit.Entry.Kind}) {hit.Entry.Content}");

        _recalledHitsForTurn = selected;
        if (_utility is not null)
        {
            try
            {
                await _utility.RecordInjectionAsync(selected, iteration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // measurement is best-effort; a ledger hiccup must not affect the turn
            }
        }

        MemoriesRecalled?.Invoke(selected.Count);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// After the turn, ask the judge which injected memories actually influenced the reply,
    /// and write the verdicts to the utility ledger. Best-effort by design.
    /// </summary>
    private async Task JudgeUtilityAsync(int iteration, CancellationToken cancellationToken)
    {
        if (_utility is null || _recalledHitsForTurn.Count == 0)
            return;

        var reply = LatestAssistantText();
        if (string.IsNullOrWhiteSpace(reply))
            return;

        try
        {
            var useful = await _utility
                .JudgeTurnAsync(_recalledHitsForTurn, LatestUserText(), reply, iteration, cancellationToken)
                .ConfigureAwait(false);
            if (useful is int count)
                UtilityJudged?.Invoke(count, _recalledHitsForTurn.Count);
        }
        catch (OperationCanceledException)
        {
            // cancellation is the normal way out of the loop
        }
        catch
        {
            // judging is best-effort; a judge hiccup must not affect the loop
        }
    }

    /// <summary>Text of the most recent assistant message, used as the judged reply.</summary>
    private string? LatestAssistantText()
    {
        for (var i = _context.Messages.Count - 1; i >= 0; i--)
        {
            var message = _context.Messages[i];
            if (message.Role == Role.Assistant && !string.IsNullOrWhiteSpace(message.Content))
                return message.Content;
        }
        return null;
    }

    /// <summary>Text of the most recent user message, used as the recall query.</summary>
    private string? LatestUserText()
    {
        for (var i = _context.Messages.Count - 1; i >= 0; i--)
        {
            if (_context.Messages[i].Role == Role.User)
                return _context.Messages[i].Content;
        }
        return null;
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        if (_compactor is null)
            return;

        try
        {
            var result = await _compactor.CompactIfNeededAsync(_context, cancellationToken).ConfigureAwait(false);
            if (result.Evicted > 0)
                Compacted?.Invoke(result.Evicted);
            if (result.Consolidated > 0)
                Consolidated?.Invoke(result.Consolidated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Compaction is best-effort; a summariser hiccup must not kill the loop.
        }
    }

    private async Task<ToolResult> InvokeToolAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ToolInput input;
        try
        {
            input = string.IsNullOrWhiteSpace(call.Arguments)
                ? ToolInput.Empty
                : ToolInput.FromJson(call.Arguments);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Could not parse arguments for '{call.Name}': {ex.Message}");
        }

        return await _tools!.InvokeAsync(call.Name, input, cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        if (_options.IterationDelay <= TimeSpan.Zero)
            return;

        try
        {
            await Task.Delay(_options.IterationDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancellation is the normal way out of the loop
        }
    }
}
