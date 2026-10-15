using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Attention;

/// <summary>How much of the memory pipeline a turn runs.</summary>
public enum MemoryLevel
{
    /// <summary>Resident memories only, which are already in the system prompt.</summary>
    None,

    /// <summary>Search with the message as written; no query rewrite, no rerank, a smaller budget.</summary>
    Light,

    /// <summary>The whole pipeline: rewrite, wide hybrid search, rerank, full budget.</summary>
    Full,
}

/// <summary>What the router decided for one turn, and what was applied.</summary>
/// <param name="Memory">The memory level applied this turn.</param>
/// <param name="Categories">Tool categories offered this turn.</param>
/// <param name="ProposedMemory">The memory level the router chose before shadowing.</param>
/// <param name="ProposedCategories">The categories the router chose before shadowing.</param>
/// <param name="Shadow">The router's choice was recorded but the full pipeline ran.</param>
/// <param name="FellBack">The decision model failed, so the full pipeline ran.</param>
/// <param name="Elapsed">Time spent deciding.</param>
public sealed record RoutingDecision(
    MemoryLevel Memory,
    IReadOnlySet<string> Categories,
    MemoryLevel ProposedMemory,
    IReadOnlySet<string> ProposedCategories,
    bool Shadow,
    bool FellBack,
    TimeSpan Elapsed)
{
    /// <summary>Fixed instructions added to this turn's context because their question came out yes.</summary>
    public IReadOnlyList<string> Guidance { get; init; } = [];
}

/// <summary>
/// An instruction the router adds to a turn when the decision model says it applies. The text is
/// fixed and written by a person; the router only decides whether it applies, never what it says.
/// </summary>
/// <param name="Id">Stable identifier, used in the question and the ledger.</param>
/// <param name="Question">A yes or no question about the latest message.</param>
/// <param name="Text">What the agent is told when the answer is yes.</param>
/// <param name="Threshold">Probability of yes needed to add it; low, because adding a reminder costs little.</param>
public sealed record RoutingGuidance(string Id, string Question, string Text, double Threshold = 0.5);

/// <summary>Knobs for context routing.</summary>
public sealed class RoutingOptions
{
    /// <summary>Tool name to category. Tools not listed are always offered.</summary>
    public IReadOnlyDictionary<string, string> ToolCategories { get; init; } = new Dictionary<string, string>();

    /// <summary>What each category is for, as the decision model reads it.</summary>
    public IReadOnlyDictionary<string, string> CategoryDescriptions { get; init; } = new Dictionary<string, string>();

    /// <summary>Memory is reduced only when the model is at least this sure it is not needed.</summary>
    public double MemoryConfidence { get; init; } = 0.8;

    /// <summary>A category is dropped only when the model is at least this sure it is not needed.</summary>
    public double DropConfidence { get; init; } = 0.8;

    /// <summary>Fraction of turns that record the router's choice but run the full pipeline.</summary>
    public double ShadowRate { get; init; } = 0.1;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Previous messages the decision model sees.</summary>
    public int RecentMessages { get; init; } = 6;

    public int MaxCharsPerMessage { get; init; } = 300;

    /// <summary>Append-only log of decisions and outcomes; null keeps none.</summary>
    public string? LedgerPath { get; init; }

    /// <summary>Fixed instructions the router may add to a turn.</summary>
    public IReadOnlyList<RoutingGuidance> Guidance { get; init; } = [];
}

/// <summary>
/// Decides, before attention assembles the context, how much of the pipeline a turn needs: how much
/// memory to recall and which tool definitions to send. Every uncertain or failed decision falls back
/// to the full pipeline, and a sample of turns runs in shadow so the router's mistakes can be measured.
/// </summary>
public sealed class ContextRouter(IDecisionModel model, RoutingOptions options, Func<int>? memoryCount = null)
{
    private const string MemoryQuestion = "memory";
    private const string ToolPrefix = "tools_";
    private const string GuidancePrefix = "guide_";

    private readonly Random _random = new();
    private readonly object _ledgerGate = new();

    public IDecisionModel Model => model;

    /// <summary>Every tool category the router knows.</summary>
    public IReadOnlySet<string> Categories { get; } = options.ToolCategories.Values.ToHashSet();

    /// <summary>The category of a tool, or null when the tool is always offered.</summary>
    public string? CategoryOf(string toolName) =>
        options.ToolCategories.TryGetValue(toolName, out var category) ? category : null;

    public async Task<RoutingDecision> RouteAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        var all = Categories.ToHashSet();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        IReadOnlyList<DecisionAnswer> answers;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            answers = await model.DecideAsync(Describe(context), Questions(all), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return new RoutingDecision(MemoryLevel.Full, all, MemoryLevel.Full, all, Shadow: false, FellBack: true, elapsed);
        }

        var memory = ChooseMemory(answers.First(a => a.QuestionId == MemoryQuestion));
        var categories = all
            .Where(c => answers.FirstOrDefault(a => a.QuestionId == ToolPrefix + c) is not { } a || a["no"] < options.DropConfidence)
            .ToHashSet();

        // Guidance only adds to a turn, so it applies in shadow turns too.
        var guidance = options.Guidance
            .Where(g => answers.FirstOrDefault(a => a.QuestionId == GuidancePrefix + g.Id) is { } a && a["yes"] >= g.Threshold)
            .Select(g => g.Text)
            .ToList();

        var shadow = _random.NextDouble() < options.ShadowRate;
        return new RoutingDecision(
            shadow ? MemoryLevel.Full : memory,
            shadow ? all : categories,
            memory,
            categories,
            shadow,
            FellBack: false,
            System.Diagnostics.Stopwatch.GetElapsedTime(started))
        {
            Guidance = guidance,
        };
    }

    /// <summary>
    /// Record what happened after a routed turn. In shadow turns this measures the router's mistakes:
    /// memory it would have skipped that turned out useful, and tools it would have hidden that were used.
    /// </summary>
    public void RecordOutcome(RoutingDecision decision, IReadOnlyCollection<string> toolsUsed, int injected, int? useful)
    {
        if (options.LedgerPath is null)
            return;

        var usedCategories = toolsUsed.Select(CategoryOf).OfType<string>().Distinct().ToList();
        var entry = new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["model"] = model.Name,
            ["shadow"] = decision.Shadow,
            ["fellBack"] = decision.FellBack,
            ["elapsedMs"] = (int)decision.Elapsed.TotalMilliseconds,
            ["proposedMemory"] = decision.ProposedMemory.ToString(),
            ["appliedMemory"] = decision.Memory.ToString(),
            ["proposedCategories"] = new JsonArray(decision.ProposedCategories.Order().Select(c => (JsonNode)c).ToArray()),
            ["usedCategories"] = new JsonArray(usedCategories.Select(c => (JsonNode)c).ToArray()),
            ["injected"] = injected,
            ["useful"] = useful,
            ["memoryMiss"] = decision.Shadow && decision.ProposedMemory != MemoryLevel.Full && useful is > 0,
            ["toolMiss"] = usedCategories.Any(c => !decision.ProposedCategories.Contains(c)),
            ["guidance"] = new JsonArray(decision.Guidance.Select(g => (JsonNode)g).ToArray()),
        };

        lock (_ledgerGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LedgerPath)!);
            File.AppendAllText(options.LedgerPath, entry.ToJsonString() + Environment.NewLine);
        }
    }

    private MemoryLevel ChooseMemory(DecisionAnswer answer)
    {
        if (answer["none"] >= options.MemoryConfidence)
            return MemoryLevel.None;
        if (answer["none"] + answer["light"] >= options.MemoryConfidence)
            return MemoryLevel.Light;
        return MemoryLevel.Full;
    }

    private IReadOnlyList<DecisionQuestion> Questions(IEnumerable<string> categories)
    {
        var questions = new List<DecisionQuestion>
        {
            new(MemoryQuestion,
                "How much of the agent's long-term memory does the latest message need?",
                ["none", "light", "full"],
                new Dictionary<string, string>
                {
                    ["none"] = "It stands on its own, such as a greeting or a general question",
                    ["light"] = "It may touch something already known about the person or the project",
                    ["full"] = "It refers back to earlier work, people, decisions, or preferences",
                }),
        };

        foreach (var category in categories.Order())
        {
            var description = options.CategoryDescriptions.TryGetValue(category, out var d) ? d : category;
            questions.Add(DecisionQuestion.YesNo(
                ToolPrefix + category, $"Could answering the latest message need tools for {description}?"));
        }

        foreach (var guidance in options.Guidance)
            questions.Add(DecisionQuestion.YesNo(GuidancePrefix + guidance.Id, guidance.Question));

        return questions;
    }

    private string Describe(AgentContext context)
    {
        var sb = new StringBuilder();
        var recent = context.Messages
            .Where(m => m.Role is Role.User or Role.Assistant && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(options.RecentMessages + 1)
            .ToList();

        for (var i = 0; i < recent.Count; i++)
        {
            var label = i == recent.Count - 1 && recent[i].Role == Role.User
                ? "Latest message"
                : recent[i].Role == Role.User ? "Person" : "Agent";
            sb.AppendLine($"{label}: {Trim(recent[i].Content!)}");
        }

        var toolsUsed = context.Messages
            .TakeLast(20)
            .Where(m => m.Role == Role.Tool && m.ToolName is not null)
            .Select(m => m.ToolName!)
            .Distinct()
            .ToList();
        sb.AppendLine();
        sb.AppendLine($"Memory entries stored: {memoryCount?.Invoke().ToString() ?? "unknown"}.");
        sb.AppendLine(toolsUsed.Count == 0 ? "No tools used recently." : $"Tools used recently: {string.Join(", ", toolsUsed)}.");
        return sb.ToString();
    }

    private string Trim(string text) =>
        text.Length <= options.MaxCharsPerMessage ? text : text[..options.MaxCharsPerMessage] + "...";
}
