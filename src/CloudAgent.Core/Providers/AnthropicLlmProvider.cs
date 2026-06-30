using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// Talks to the Anthropic Messages API over HTTP, including tool use. No SDK dependency —
/// just the contract. Tool calls arrive as <c>tool_use</c> content blocks and are fed back
/// as <c>tool_result</c> blocks on a following user message.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public AnthropicLlmProvider(string apiKey, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An Anthropic API key is required.", nameof(apiKey));

        _apiKey = apiKey;
        _http = http ?? new HttpClient();
    }

    public string Name => "anthropic";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model ?? "claude-opus-4-8",
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = BuildMessages(request.Messages),
        };

        if (!string.IsNullOrWhiteSpace(request.System))
            body["system"] = request.System;

        if (request.Tools.Count > 0)
            body["tools"] = BuildTools(request.Tools);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);

        using var httpResponse = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Anthropic API returned {(int)httpResponse.StatusCode}: {responseBody}");

        return Parse(responseBody);
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.Schema.DeepClone(),
            });
        }
        return array;
    }

    private static JsonArray BuildMessages(IReadOnlyList<Message> messages)
    {
        var result = new JsonArray();
        JsonArray? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is null)
                return;
            result.Add(new JsonObject { ["role"] = "user", ["content"] = pendingToolResults });
            pendingToolResults = null;
        }

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case Role.Tool:
                    pendingToolResults ??= [];
                    pendingToolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Content,
                    });
                    break;

                case Role.Assistant when message.ToolCalls.Count > 0:
                    FlushToolResults();
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(message.Content))
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                    foreach (var call in message.ToolCalls)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = ParseArgumentsOrEmpty(call.Arguments),
                        });
                    }
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;

                case Role.Assistant:
                    FlushToolResults();
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = message.Content });
                    break;

                case Role.User:
                    FlushToolResults();
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = message.Content });
                    break;

                // System messages are carried out-of-band in the top-level "system" field.
            }
        }

        FlushToolResults();
        return result;
    }

    private static JsonNode ParseArgumentsOrEmpty(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(arguments) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static LlmResponse Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                        text.Append(block.GetProperty("text").GetString());
                        break;
                    case "tool_use":
                        toolCalls.Add(new ToolCall(
                            block.GetProperty("id").GetString() ?? "",
                            block.GetProperty("name").GetString() ?? "",
                            block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
                        break;
                }
            }
        }

        var stopReason = (root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null) switch
        {
            "end_turn" => StopReason.EndTurn,
            "max_tokens" => StopReason.MaxTokens,
            _ => StopReason.Other,
        };

        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        return new LlmResponse(text.ToString(), stopReason, inputTokens, outputTokens)
        {
            ToolCalls = toolCalls,
        };
    }
}
