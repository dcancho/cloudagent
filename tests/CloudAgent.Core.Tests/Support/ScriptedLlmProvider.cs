using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Tests.Support;

/// <summary>
/// A deterministic model: each request is answered by the first rule whose predicate matches, and
/// every request is kept so tests can assert what was sent.
/// </summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly List<(Func<LlmRequest, bool> When, Func<LlmRequest, LlmResponse> Respond)> _rules = [];
    private readonly List<LlmRequest> _requests = [];

    public string Name => "scripted";

    public IReadOnlyList<LlmRequest> Requests => _requests;

    public ScriptedLlmProvider When(Func<LlmRequest, bool> predicate, Func<LlmRequest, LlmResponse> respond)
    {
        _rules.Add((predicate, respond));
        return this;
    }

    public ScriptedLlmProvider When(Func<LlmRequest, bool> predicate, string text) =>
        When(predicate, _ => new LlmResponse(text, InputTokens: 10, OutputTokens: 5));

    public ScriptedLlmProvider Otherwise(string text) => When(_ => true, text);

    public ScriptedLlmProvider Throws(Func<LlmRequest, bool> predicate) =>
        When(predicate, _ => throw new HttpRequestException("scripted failure"));

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        foreach (var (when, respond) in _rules)
        {
            if (when(request))
                return Task.FromResult(respond(request));
        }

        throw new InvalidOperationException("No scripted rule matches this request.");
    }

    public static bool IsJudge(LlmRequest request) => request.System?.StartsWith("You audit") == true;

    public static bool IsReranker(LlmRequest request) => request.System?.StartsWith("You rank") == true;

    public static bool IsRewriter(LlmRequest request) => request.System?.StartsWith("You rewrite") == true;
}
