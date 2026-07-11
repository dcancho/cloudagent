using System;
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
    private readonly Attention.IAttentionManager _attention;
    private readonly Memory.UtilityTracker? _utility;

    /// <summary>What attention assembled for the current turn: system prefix, volatile context,
    /// and the injected hits (kept for the feedback loop's recording and post-turn judging).</summary>
    private Attention.AssembledContext? _assembled;

    public AgentLoop(
        ILlmProvider provider,
        AgentOptions? options = null,
        IInputSource? input = null,
        AgentContext? context = null,
        ToolRegistry? tools = null,
        ContextCompactor? compactor = null,
        Attention.IAttentionManager? attention = null,
        Memory.UtilityTracker? utility = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new AgentOptions();
        _input = input ?? new NullInputSource();
        _context = context ?? new AgentContext();
        _tools = tools;
        _compactor = compactor;
        // Even without memory, the loop needs an assembler for the persona / system prompt.
        _attention = attention ?? new Attention.ContextAssembler(
            _options.SystemPrompt, retriever: null, new Attention.AttentionOptions());
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

            // 2. Attention: assemble what reaches the model this turn (residents, state, summary,
            // recalled memories). Attention returns the injected hits; the loop feeds the ledger.
            _assembled = await _attention
                .AssembleAsync(_context, LatestUserText(), cancellationToken).ConfigureAwait(false);
            if (_assembled.InjectedMemories.Count > 0)
            {
                await RecordInjectionAsync(iteration, cancellationToken).ConfigureAwait(false);
                MemoriesRecalled?.Invoke(_assembled.InjectedMemories.Count);
            }

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
                System = _assembled!.System,
                EphemeralContext = _assembled.EphemeralContext,
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

    /// <summary>
    /// Record the memories attention injected into the utility ledger — the feedback loop (eje 7).
    /// Attention itself no longer touches the ledger; it hands back the hits and the loop records
    /// them. Best-effort: a ledger hiccup must not affect the turn.
    /// </summary>
    private async Task RecordInjectionAsync(int iteration, CancellationToken cancellationToken)
    {
        if (_utility is null || _assembled is null)
            return;

        try
        {
            await _utility
                .RecordInjectionAsync(_assembled.InjectedMemories, iteration, cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// After the turn, ask the judge which injected memories actually influenced the reply,
    /// and write the verdicts to the utility ledger. Best-effort by design.
    /// </summary>
    private async Task JudgeUtilityAsync(int iteration, CancellationToken cancellationToken)
    {
        if (_utility is null || _assembled is null || _assembled.InjectedMemories.Count == 0)
            return;

        var reply = LatestAssistantText();
        if (string.IsNullOrWhiteSpace(reply))
            return;

        try
        {
            var useful = await _utility
                .JudgeTurnAsync(_assembled.InjectedMemories, LatestUserText(), reply, iteration, cancellationToken)
                .ConfigureAwait(false);
            if (useful is int count)
                UtilityJudged?.Invoke(count, _assembled.InjectedMemories.Count);
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
