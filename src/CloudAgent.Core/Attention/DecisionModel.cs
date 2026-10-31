using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Attention;

/// <summary>A question with a fixed set of answers.</summary>
/// <param name="Id">Stable identifier, used to read the answer back.</param>
/// <param name="Text">The question as the decision model reads it.</param>
/// <param name="Options">The only answers allowed.</param>
/// <param name="Criteria">What each option means, when the option name alone is not enough.</param>
public sealed record DecisionQuestion(
    string Id,
    string Text,
    IReadOnlyList<string> Options,
    IReadOnlyDictionary<string, string>? Criteria = null)
{
    public static DecisionQuestion YesNo(string id, string text) => new(id, text, ["yes", "no"]);

    /// <summary>Whether this is a yes or no question.</summary>
    public bool IsYesNo => Options is ["yes", "no"];

    public string Describe(string option) =>
        Criteria is not null && Criteria.TryGetValue(option, out var description) ? description : option;
}

/// <summary>A probability for each option of one question; the probabilities sum to one.</summary>
public sealed record DecisionAnswer(string QuestionId, IReadOnlyDictionary<string, double> Probabilities)
{
    public double this[string option] => Probabilities.TryGetValue(option, out var p) ? p : 0;
}

/// <summary>
/// A model that answers fixed questions about a situation with a probability per option, instead of
/// generating text. Implementations throw when they cannot answer; callers fall back to the safe path.
/// </summary>
public interface IDecisionModel
{
    string Name { get; }

    Task<IReadOnlyList<DecisionAnswer>> DecideAsync(
        string situation, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellationToken = default);
}

/// <summary>
/// A decision model built on any chat model: it asks for probabilities as JSON. Slower and less
/// calibrated than a dedicated decision model, but it works wherever a chat model does, so routing can
/// be exercised before one is deployed.
/// </summary>
public sealed class GenerativeDecisionModel(ILlmProvider provider, string? model = null) : IDecisionModel
{
    public string Name => $"generative ({model ?? provider.Name})";

    public async Task<IReadOnlyList<DecisionAnswer>> DecideAsync(
        string situation, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellationToken = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Situation:").AppendLine(situation).AppendLine();
        prompt.AppendLine("Questions:");
        foreach (var q in questions)
        {
            var options = q.Criteria is null
                ? string.Join(", ", q.Options)
                : string.Join("; ", q.Options.Select(o => $"{o} ({q.Describe(o)})"));
            prompt.AppendLine($"- {q.Id}: {q.Text} Options: {options}");
        }

        var request = new LlmRequest
        {
            System = "You classify a conversation turn. For every question, give a probability for each of its " +
                     "options, summing to 1, reflecting how sure you are. Reply with only a JSON object mapping " +
                     "each question id to an object of option to probability. No other text.",
            Messages = [Message.User(prompt.ToString())],
            Model = model,
            Purpose = LlmPurpose.Router,
            MaxTokens = 400,
            Temperature = 0,
        };

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, questions);
    }

    /// <summary>Read the JSON reply; anything missing or malformed is an error, not a guess.</summary>
    public static IReadOnlyList<DecisionAnswer> Parse(string text, IReadOnlyList<DecisionQuestion> questions)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("The decision reply contained no JSON object.");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(text[start..(end + 1)]) as JsonObject
                ?? throw new FormatException("The decision reply was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The decision reply was not valid JSON: {ex.Message}");
        }

        return questions.Select(q =>
        {
            if (root[q.Id] is not JsonObject answer)
                throw new FormatException($"The decision reply did not answer '{q.Id}'.");

            var raw = q.Options.ToDictionary(o => o, o => Math.Max(0, ReadNumber(answer[o])));
            var total = raw.Values.Sum();
            if (total <= 0)
                throw new FormatException($"The decision reply gave no probability for '{q.Id}'.");

            return new DecisionAnswer(q.Id, raw.ToDictionary(kv => kv.Key, kv => kv.Value / total));
        }).ToList();
    }

    private static double ReadNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
}
