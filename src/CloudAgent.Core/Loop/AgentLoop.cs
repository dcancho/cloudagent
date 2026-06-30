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

    /// <summary>Scopes consulted on automatic recall each turn.</summary>
    private static readonly Memory.MemoryScope[] RecallScopes =
        [Memory.MemoryScope.Personal, Memory.MemoryScope.Project, Memory.MemoryScope.Session];

    /// <summary>Formatted memories recalled for the current turn, injected into the system prompt.</summary>
    private string? _recalledForTurn;

    public AgentLoop(
        ILlmProvider provider,
        AgentOptions? options = null,
        IInputSource? input = null,
        AgentContext? context = null,
        ToolRegistry? tools = null,
        ContextCompactor? compactor = null,
        Memory.MemoryManager? memory = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new AgentOptions();
        _input = input ?? new NullInputSource();
        _context = context ?? new AgentContext();
        _tools = tools;
        _compactor = compactor;
        _memory = memory;
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
            _recalledForTurn = await RecallAsync(cancellationToken).ConfigureAwait(false);

            // 3. Think and act until the model produces a plain answer.
            try
            {
                await ThinkAndActAsync(iteration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

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
                _context.Add(Message.ToolResult(call.Id, call.Name, content));
                ToolInvoked?.Invoke(call, result);
            }
        }
    }

    /// <summary>System prompt plus the rolling summary and any recalled memories.</summary>
    private string ComposeSystem()
    {
        var sb = new StringBuilder(_options.SystemPrompt);

        if (!string.IsNullOrWhiteSpace(_context.RollingSummary))
            sb.Append("\n\n## Summary of earlier conversation\n").Append(_context.RollingSummary);

        if (!string.IsNullOrWhiteSpace(_recalledForTurn))
            sb.Append("\n\n## Relevant memories\n").Append(_recalledForTurn);

        return sb.ToString();
    }

    /// <summary>
    /// Recall memories relevant to the latest turn: always the top-K, plus any others scoring
    /// at or above the recall threshold (capped). Returns a formatted block, or null if none.
    /// </summary>
    private async Task<string?> RecallAsync(CancellationToken cancellationToken)
    {
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

        MemoriesRecalled?.Invoke(selected.Count);
        return sb.ToString().TrimEnd();
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
