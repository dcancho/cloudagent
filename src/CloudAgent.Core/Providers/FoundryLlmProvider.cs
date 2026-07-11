using System;
using System.Collections.Generic;
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
    private readonly ChatCompletionsClient _client;
    private readonly string? _defaultModel;

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
        var options = new ChatCompletionsOptions
        {
            Model = request.Model ?? _defaultModel,
            MaxTokens = request.MaxTokens,
            Temperature = (float)request.Temperature,
        };

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

        Response<ChatCompletions> response =
            await _client.CompleteAsync(options, cancellationToken).ConfigureAwait(false);

        return MapResponse(response.Value);
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
            completion.Content ?? string.Empty,
            stopReason,
            completion.Usage?.PromptTokens,
            completion.Usage?.CompletionTokens)
        {
            ToolCalls = toolCalls,
        };
    }
}
