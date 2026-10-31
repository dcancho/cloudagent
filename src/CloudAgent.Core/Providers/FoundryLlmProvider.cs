using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.Inference;
using Azure.Core;
using Azure.Identity;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// Accesses the Microsoft Foundry model catalog in chat-completions mode via the
/// Azure AI Inference SDK (<c>ChatCompletionsClient</c>, the <c>/chat/completions</c> route),
/// including tool calling.
///
/// One endpoint serves the whole catalog; pick the catalog model per request via
/// <see cref="LlmRequest.Model"/> (e.g. "DeepSeek-V3.1", "mistral-large", "gpt-4o-mini").
/// </summary>
public sealed class FoundryLlmProvider : ILlmProvider
{
    /// <summary>
    /// Reasoning tokens count against <c>max_completion_tokens</c>, so a budget sized for a short
    /// answer would be spent thinking. Small budgets are raised to leave room for both.
    /// </summary>
    private const int MinReasoningBudget = 4096;

    /// <summary>
    /// Some OpenAI models emit citation placeholders delimited by private-use characters
    /// (U+E200 to U+E201) that refer to a search tool of their own platform; they mean nothing here.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CitationMarkers =
        new("[^]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ChatCompletionsClient _client;
    private readonly string? _defaultModel;
    private readonly ConcurrentDictionary<string, ModelQuirks> _quirks = new();

    /// <param name="endpoint">
    /// The Foundry models endpoint, e.g. <c>https://&lt;resource&gt;.services.ai.azure.com/models</c>.
    /// </param>
    /// <param name="apiKey">
    /// API key for the resource. If null/empty, falls back to <see cref="DefaultAzureCredential"/>
    /// (Entra ID — managed identity, Azure CLI login, etc.).
    /// </param>
    /// <param name="defaultModel">Catalog model to use when a request doesn't specify one.</param>
    public FoundryLlmProvider(Uri endpoint, string? apiKey = null, string? defaultModel = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = new AzureAIInferenceClientOptions();
        _client = string.IsNullOrWhiteSpace(apiKey)
            ? new ChatCompletionsClient(endpoint, (TokenCredential)new DefaultAzureCredential(), options)
            : new ChatCompletionsClient(endpoint, new AzureKeyCredential(apiKey), options);

        _defaultModel = defaultModel;
    }

    public string Name => "foundry";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _defaultModel ?? string.Empty;
        var quirks = _quirks.GetOrAdd(model, _ => new ModelQuirks());

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Response<ChatCompletions> response = await _client
                    .CompleteAsync(BuildOptions(request, model, quirks), cancellationToken)
                    .ConfigureAwait(false);
                return MapResponse(response.Value) with { CacheReadTokens = CachedTokens(response.GetRawResponse()) };
            }
            catch (RequestFailedException ex) when (ex.Status == 400 && attempt < 3 && quirks.LearnFrom(ex.Message))
            {
            }
        }
    }

    /// <summary>
    /// Reasoning models reject parameters that older chat models accept: they want
    /// <c>max_completion_tokens</c> instead of <c>max_tokens</c>, may accept only the default
    /// temperature, and some refuse function tools on this route unless reasoning is turned off.
    /// The provider learns each rule from the first rejection, per model, and retries.
    /// </summary>
    private sealed class ModelQuirks
    {
        public volatile bool UseCompletionTokens;
        public volatile bool OmitTemperature;
        public volatile bool NoReasoningWithTools;

        public bool LearnFrom(string error)
        {
            if (!UseCompletionTokens && error.Contains("max_completion_tokens", StringComparison.Ordinal))
                return UseCompletionTokens = true;
            if (!NoReasoningWithTools && error.Contains("reasoning_effort", StringComparison.Ordinal)
                && error.Contains("tools", StringComparison.OrdinalIgnoreCase))
                return NoReasoningWithTools = true;
            if (!OmitTemperature && error.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                return OmitTemperature = true;
            return false;
        }
    }

    private ChatCompletionsOptions BuildOptions(LlmRequest request, string model, ModelQuirks quirks)
    {
        var options = new ChatCompletionsOptions { Model = model.Length > 0 ? model : null };

        if (quirks.UseCompletionTokens)
        {
            var budget = Math.Max(request.MaxTokens, MinReasoningBudget);
            options.AdditionalProperties["max_completion_tokens"] = BinaryData.FromString(budget.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            options.MaxTokens = request.MaxTokens;
        }

        if (!quirks.OmitTemperature)
            options.Temperature = (float)request.Temperature;

        if (quirks.NoReasoningWithTools && request.Tools.Count > 0)
            options.AdditionalProperties["reasoning_effort"] = BinaryData.FromString("\"none\"");

        // The system message stays stable across turns so Azure's automatic prefix caching keeps
        // hitting it; per-turn volatile context is folded into the latest human turn instead.
        if (!string.IsNullOrWhiteSpace(request.System))
            options.Messages.Add(new ChatRequestSystemMessage(request.System));

        var ephemeralTarget = string.IsNullOrWhiteSpace(request.EphemeralContext)
            ? -1
            : LastUserIndex(request.Messages);

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            if (i == ephemeralTarget)
                message = message with { Content = $"{request.EphemeralContext}\n\n{message.Content}" };
            options.Messages.Add(MapMessage(message));
        }

        foreach (var tool in request.Tools)
        {
            options.Tools.Add(new ChatCompletionsToolDefinition(
                new FunctionDefinition(tool.Name)
                {
                    Description = tool.Description,
                    Parameters = BinaryData.FromString(tool.Schema.ToJsonString()),
                }));
        }

        return options;
    }

    /// <summary>Index of the most recent human turn — where per-turn context is folded in.</summary>
    private static int LastUserIndex(IReadOnlyList<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == Role.User)
                return i;
        return -1;
    }

    private static ChatRequestMessage MapMessage(Message message)
    {
        switch (message.Role)
        {
            case Role.System:
                return new ChatRequestSystemMessage(message.Content);

            case Role.Tool:
                return new ChatRequestToolMessage(message.Content, message.ToolCallId);

            case Role.Assistant when message.ToolCalls.Count > 0:
                var assistant = new ChatRequestAssistantMessage(message.Content ?? string.Empty);
                foreach (var call in message.ToolCalls)
                    assistant.ToolCalls.Add(new ChatCompletionsToolCall(
                        call.Id, new FunctionCall(call.Name, call.Arguments)));
                return assistant;

            case Role.Assistant:
                return new ChatRequestAssistantMessage(message.Content);

            default:
                return new ChatRequestUserMessage(message.Content);
        }
    }

    /// <summary>The prompt tokens the service served from its prefix cache, read from the raw usage block; null when absent.</summary>
    public static int? CachedTokens(Response raw)
    {
        try
        {
            using var body = System.Text.Json.JsonDocument.Parse(raw.Content.ToMemory());
            return body.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var cached)
                && cached.TryGetInt32(out var tokens)
                    ? tokens
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static LlmResponse MapResponse(ChatCompletions completion)
    {
        var toolCalls = new List<ToolCall>();
        foreach (var call in completion.ToolCalls)
        {
            if (call is ChatCompletionsToolCall fn)
                toolCalls.Add(new ToolCall(fn.Id, fn.Function.Name, fn.Function.Arguments));
        }

        var stopReason = completion.FinishReason switch
        {
            { } fr when fr == CompletionsFinishReason.TokenLimitReached => StopReason.MaxTokens,
            { } fr when fr == CompletionsFinishReason.Stopped => StopReason.EndTurn,
            _ => StopReason.Other,
        };

        return new LlmResponse(
            CitationMarkers.Replace(completion.Content ?? string.Empty, string.Empty),
            stopReason,
            completion.Usage?.PromptTokens,
            completion.Usage?.CompletionTokens)
        {
            ToolCalls = toolCalls,
        };
    }
}
