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

    private readonly ILlmProvider _provider;
    private readonly AgentOptions _options;
    private readonly IInputSource _input;
    private readonly AgentContext _context;
    private readonly ToolRegistry? _tools;
    private readonly ContextCompactor? _compactor;
    private readonly Attention.IAttentionManager _attention;
    private readonly Memory.UtilityTracker? _utility;
    private readonly Attention.ContextRouter? _router;

    /// <summary>This turn's routing decision, the tools it used, and how many recalled memories helped.</summary>
    private Attention.RoutingDecision? _decision;
    private readonly List<string> _turnTools = [];
    private int? _turnUseful;

    private const string RequestToolsName = "request_tools";

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
        Memory.UtilityTracker? utility = null,
        Attention.ContextRouter? router = null)
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
        _router = router;
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

    /// <summary>Raised when an iteration failed, with the iteration number and the error. The loop keeps running.</summary>
    public event Action<int, Exception>? TurnFailed;

    /// <summary>Raised after every iteration, successful or not, once the context is settled.</summary>
    public event Action<int>? TurnEnded;

    /// <summary>Raised when the context router decided how much of the pipeline this turn runs.</summary>
    public event Action<Attention.RoutingDecision>? Routed;

    /// <summary>The id this agent's model calls are accounted to, when it has one.</summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Run the loop until <paramref name="cancellationToken"/> trips or
    /// <see cref="AgentOptions.MaxIterations"/> is reached.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var usage = Providers.UsageScope.Begin(AgentId);
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

            // A spent token budget is answered without calling the model at all.
            if (_options.Budget is { Exhausted: true } budget)
            {
                if (stimulus is not null)
                {
                    var text = $"I've used this workspace's token budget ({budget.Used:N0} of {budget.Limit:N0} tokens), " +
                               "so I'm not doing more work until it is raised.";
                    _context.Add(Message.Assistant(text, []));
                    ResponseReceived?.Invoke(iteration, new LlmResponse(text));
                    TurnEnded?.Invoke(iteration);
                }
                await DelayAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                // 1.5. Routing: decide how much memory to recall and which tools to offer this turn.
                _turnTools.Clear();
                _turnUseful = null;
                _assembled = null;
                _decision = null;
                _decision = _router is null
                    ? null
                    : await _router.RouteAsync(_context, cancellationToken).ConfigureAwait(false);
                if (_decision is not null)
                    Routed?.Invoke(_decision);

                // 2. Attention: assemble what reaches the model this turn (residents, state, summary,
                // recalled memories). Attention returns the injected hits; the loop feeds the ledger.
                _assembled = await _attention
                    .AssembleAsync(_context, LatestUserText(), _decision?.Memory ?? Attention.MemoryLevel.Full, cancellationToken)
                    .ConfigureAwait(false);
                if (_decision is { Guidance.Count: > 0 })
                {
                    var guidance = Attention.AttentionRendering.GuidanceHeader + "\n" +string.Join('\n', _decision.Guidance.Select(g => $"- {g}"));
                    _assembled = _assembled with
                    {
                        EphemeralContext = string.IsNullOrWhiteSpace(_assembled.EphemeralContext)
                            ? guidance
                            : $"{_assembled.EphemeralContext}\n\n{guidance}",
                    };
                }

                // 3. Think and act until the model produces a plain answer.
                await ThinkAndActAsync(iteration, cancellationToken).ConfigureAwait(false);

                // 3.5. Close the feedback loop: did the injected memories influence the reply?
                await JudgeUtilityAsync(iteration, cancellationToken).ConfigureAwait(false);

                _context.AgeToolResults(_options.KeepToolResultTurns, _options.AgedToolResultChars);

                // 4. Keep working memory under budget; evicted turns become episodic memory.
                await CompactAsync(cancellationToken).ConfigureAwait(false);

                RecordRoutingOutcome(Attention.TurnOutcome.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordRoutingOutcome(Attention.TurnOutcome.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                // A failed turn must not end the agent: report it and wait for the next stimulus.
                RecordRoutingOutcome(Attention.TurnOutcome.Failed);
                TurnFailed?.Invoke(iteration, ex);
            }

            TurnEnded?.Invoke(iteration);

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ThinkAndActAsync(int iteration, CancellationToken cancellationToken)
    {
        var allDefinitions = _tools?.GetDefinitions() ?? [];
        var offered = _decision is null ? null : new HashSet<string>(_decision.Categories);

        var points = 0;
        for (var hop = 0; ; hop++)
        {
            var toolDefinitions = SelectTools(allDefinitions, offered);
            var outOfBudget = _options.Budget?.Exhausted == true;
            var outOfPoints = _options.MaxToolPoints > 0 && points >= _options.MaxToolPoints;
            var lastStep = hop >= _options.MaxToolSteps || outOfPoints || outOfBudget;
            string? limit = null;
            if (lastStep)
            {
                var reason = outOfBudget
                    ? "This workspace has used its token budget, so you cannot call tools now and will not get another turn until it is raised."
                    : $"You have used this turn's tool budget ({points} points over {hop} steps), so you cannot call tools now.";
                limit = reason + " Reply to the person in their language: what you found or did, what is left, and what you need from them to continue.";
            }
            var fit = InputCap.Fit(
                new LlmRequest
                {
                    System = _assembled!.System,
                    EphemeralContext = _assembled.EphemeralContext,
                    Messages = _context.Messages,
                    Model = _options.Model,
                    Purpose = LlmPurpose.Turn,
                    MaxTokens = _options.MaxTokens,
                    Temperature = _options.Temperature,
                    Tools = lastStep ? [] : toolDefinitions,
                },
                _options.MaxInputTokens,
                limit,
                _assembled.InjectedMemories.Count);
            var request = fit.Request;

            if (hop == 0)
            {
                if (fit.RecalledKept < _assembled.InjectedMemories.Count)
                    _assembled = _assembled with { InjectedMemories = _assembled.InjectedMemories.Take(fit.RecalledKept).ToList() };
                if (_assembled.InjectedMemories.Count > 0)
                {
                    await RecordInjectionAsync(iteration, cancellationToken).ConfigureAwait(false);
                    MemoriesRecalled?.Invoke(_assembled.InjectedMemories.Count);
                }
            }

            LlmResponse response;
            try
            {
                response = await _provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Providers.TokenBudgetExceededException)
            {
                response = new LlmResponse("I've used this workspace's token budget before finishing, so I'm stopping here.");
            }

            // Out of steps, the turn ends in a reply even if the model asked for more tools anyway;
            // those calls are dropped so the history never holds a call without a result.
            if (lastStep && response.HasToolCalls)
            {
                response = new LlmResponse(
                    string.IsNullOrWhiteSpace(response.Text)
                        ? outOfBudget
                            ? "I've used this workspace's token budget before finishing, so I'm stopping here."
                            : $"I used this turn's tool budget before finishing. Tell me to continue and I will pick up from here."
                        : response.Text,
                    response.StopReason, response.InputTokens, response.OutputTokens)
                {
                    CacheReadTokens = response.CacheReadTokens,
                    CacheWriteTokens = response.CacheWriteTokens,
                };
            }

            // Record the assistant turn (text plus any tool calls it requested).
            _context.Add(Message.Assistant(response.Text, response.ToolCalls));
            ResponseReceived?.Invoke(iteration, response);

            // No tools requested (or none available) — the turn is done.
            if (!response.HasToolCalls || _tools is null)
                return;

            // Run each requested tool and feed its result back as a tool message.
            foreach (var call in response.ToolCalls)
            {
                _turnTools.Add(call.Name);
                points += ToolWeights.Of(call.Name);
                var result = call.Name == RequestToolsName && offered is not null
                    ? RequestTools(call, offered)
                    : await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);
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
            {
                _turnUseful = count;
                UtilityJudged?.Invoke(count, _assembled.InjectedMemories.Count);
            }
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

    /// <summary>
    /// The tools offered this pass: everything without routing; otherwise the tools in offered
    /// categories, the uncategorized ones, and <c>request_tools</c> while anything is held back.
    /// </summary>
    private IReadOnlyList<ToolDefinition> SelectTools(IReadOnlyList<ToolDefinition> all, HashSet<string>? offered)
    {
        if (offered is null || _router is null)
            return all;

        var selected = new List<ToolDefinition>();
        var withheld = new SortedSet<string>();
        foreach (var definition in all)
        {
            var category = _router.CategoryOf(definition.Name);
            if (category is null || offered.Contains(category))
                selected.Add(definition);
            else
                withheld.Add(category);
        }

        if (withheld.Count > 0)
            selected.Add(RequestToolsDefinition(withheld));
        return selected;
    }

    private static ToolDefinition RequestToolsDefinition(IEnumerable<string> withheld) => new(
        RequestToolsName,
        $"Make more tools available for the rest of this turn. Categories not offered now: {string.Join(", ", withheld)}.",
        new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "object",
            ["properties"] = new System.Text.Json.Nodes.JsonObject
            {
                ["categories"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Comma-separated category names.",
                },
            },
            ["required"] = new System.Text.Json.Nodes.JsonArray("categories"),
        });

    private ToolResult RequestTools(ToolCall call, HashSet<string> offered)
    {
        string requested;
        try
        {
            requested = ToolInput.FromJson(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments)
                .GetString("categories");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        var added = requested
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => _router!.Categories.Contains(c) && offered.Add(c))
            .ToList();
        return ToolResult.Ok(added.Count == 0
            ? "Those categories were already available or do not exist."
            : $"Now available: {string.Join(", ", added)}.");
    }

    private void RecordRoutingOutcome(Attention.TurnOutcome outcome)
    {
        if (_router is null || _decision is null)
            return;

        try
        {
            _router.RecordOutcome(
                _decision, _turnTools.Where(t => t != RequestToolsName).ToList(),
                _assembled?.InjectedMemories.Count ?? 0, _turnUseful, outcome);
        }
        catch
        {
            // the routing record is best-effort; it must not affect the turn
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
