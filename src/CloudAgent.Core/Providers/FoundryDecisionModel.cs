using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using CloudAgent.Core.Attention;

namespace CloudAgent.Core.Providers;

/// <summary>
/// A decision model served by Microsoft Foundry's decision API (<c>/providers/microsoft/v1/systemone</c>),
/// such as Microsoft-Decision-1. Yes or no questions are sent as <c>noul</c> questions and every other
/// question as a <c>choice</c>; all of them share one request and one state.
/// </summary>
public sealed class FoundryDecisionModel : IDecisionModel
{
    private static readonly string[] TokenScopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _deployment;
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;

    /// <param name="endpoint">The full decision endpoint, ending in <c>/providers/microsoft/v1/systemone</c>.</param>
    /// <param name="deployment">The deployment name, sent as the request's <c>model</c>.</param>
    /// <param name="apiKey">Resource key; when empty, Entra ID credentials are used.</param>
    public FoundryDecisionModel(Uri endpoint, string deployment, string? apiKey = null, HttpClient? http = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _deployment = string.IsNullOrWhiteSpace(deployment)
            ? throw new ArgumentException("A deployment name is required.", nameof(deployment))
            : deployment;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _credential = _apiKey is null ? new DefaultAzureCredential() : null;
        _http = http ?? new HttpClient();
    }

    public string Name => $"foundry decision ({_deployment})";

    public async Task<IReadOnlyList<DecisionAnswer>> DecideAsync(
        string situation, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(BuildBody(situation, questions).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
        {
            request.Headers.Add("api-key", _apiKey);
        }
        else
        {
            var token = await _credential!.GetTokenAsync(new TokenRequestContext(TokenScopes), cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The decision API returned {(int)response.StatusCode}: {body}");

        return ParseAnswers(body, questions);
    }

    public JsonObject BuildBody(string situation, IReadOnlyList<DecisionQuestion> questions)
    {
        var questionsJson = new JsonObject();
        foreach (var q in questions)
        {
            if (q.IsYesNo)
            {
                var noul = new JsonObject { ["type"] = "noul", ["instructions"] = q.Text };
                if (q.Criteria is not null)
                    noul["criteria"] = new JsonObject { ["true"] = q.Describe("yes"), ["false"] = q.Describe("no") };
                questionsJson[q.Id] = noul;
            }
            else
            {
                var criteria = new JsonObject();
                foreach (var option in q.Options)
                    criteria[option] = q.Describe(option);
                questionsJson[q.Id] = new JsonObject { ["type"] = "choice", ["instructions"] = q.Text, ["criteria"] = criteria };
            }
        }

        return new JsonObject { ["model"] = _deployment, ["state"] = situation, ["questions"] = questionsJson };
    }

    /// <summary>Read the response; a missing answer or option is an error, not a guess.</summary>
    public static IReadOnlyList<DecisionAnswer> ParseAnswers(string body, IReadOnlyList<DecisionQuestion> questions)
    {
        JsonObject answers;
        try
        {
            answers = JsonNode.Parse(body)?["answers"] as JsonObject
                ?? throw new FormatException("The decision API returned no answers.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The decision API returned invalid JSON: {ex.Message}");
        }

        return questions.Select(q =>
        {
            if (answers[q.Id] is not JsonObject answer)
                throw new FormatException($"The decision API did not answer '{q.Id}'.");

            if (q.IsYesNo)
            {
                var yes = Number(answer["noul"]) ?? throw new FormatException($"No probability for '{q.Id}'.");
                return new DecisionAnswer(q.Id, new Dictionary<string, double> { ["yes"] = yes, ["no"] = 1 - yes });
            }

            if (answer["probabilities"] is not JsonObject probabilities)
                throw new FormatException($"No probabilities for '{q.Id}'.");

            var values = q.Options.ToDictionary(
                o => o,
                o => Number(probabilities[o]) ?? throw new FormatException($"No probability for '{q.Id}' option '{o}'."));
            return new DecisionAnswer(q.Id, values);
        }).ToList();
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
