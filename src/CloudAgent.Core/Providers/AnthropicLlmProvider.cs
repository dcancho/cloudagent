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
            ["messages"] = BuildMessages(request.Messages, request.EphemeralContext),
        };

        // The stable system prefix is marked cacheable; per-turn volatile context rides at the
        // message tail instead (see BuildMessages), so this prefix stays a cache hit across turns.
        if (!string.IsNullOrWhiteSpace(request.System))
            body["system"] = new JsonArray { TextBlock(request.System, cache: true) };

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

        // Cache the whole (stable) tool-schema block: a breakpoint on the last tool caches
        // every tool before it. Tool definitions are large and never change mid-conversation.
        if (array.Count > 0 && array[^1] is JsonObject last)
            last["cache_control"] = CacheControl();

        return array;
    }

    private static JsonArray BuildMessages(IReadOnlyList<Message> messages, string? ephemeralContext)
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

        // Volatile per-turn context rides just ahead of the latest human turn, so it never
        // pollutes the cacheable system/tool/history prefix.
        PrependEphemeralToLatestUserTurn(result, ephemeralContext);

        // Incremental conversation caching: a breakpoint on the last block means each new turn
        // reads the entire prior conversation from cache and pays fresh only for the new tail.
        MarkTailCacheable(result);

        return result;
    }

    /// <summary>
    /// Prepend the volatile context to the most recent human turn (a user message with plain
    /// string content — tool-result user messages carry array content and are skipped).
    /// </summary>
    private static void PrependEphemeralToLatestUserTurn(JsonArray messages, string? ephemeralContext)
    {
        if (string.IsNullOrWhiteSpace(ephemeralContext))
            return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message
                && (string?)message["role"] == "user"
                && message["content"] is JsonValue value
                && value.TryGetValue(out string? text))
            {
                message["content"] = $"{ephemeralContext}\n\n{text}";
                return;
            }
        }
    }

    /// <summary>Attach a cache breakpoint to the last content block of the last message,
    /// normalising plain-string content to a one-block array so the marker has somewhere to sit.</summary>
    private static void MarkTailCacheable(JsonArray messages)
    {
        if (messages.Count == 0 || messages[^1] is not JsonObject last)
            return;

        if (last["content"] is JsonValue value && value.TryGetValue(out string? text))
            last["content"] = new JsonArray { TextBlock(text, cache: true) };
        else if (last["content"] is JsonArray blocks && blocks.Count > 0 && blocks[^1] is JsonObject block)
            block["cache_control"] = CacheControl();
    }

    private static JsonObject TextBlock(string text, bool cache)
    {
        var block = new JsonObject { ["type"] = "text", ["text"] = text };
        if (cache)
            block["cache_control"] = CacheControl();
        return block;
    }

    private static JsonObject CacheControl() => new() { ["type"] = "ephemeral" };

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

        int? inputTokens = null, outputTokens = null, cacheRead = null, cacheWrite = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cw)) cacheWrite = cw.GetInt32();
        }

        return new LlmResponse(text.ToString(), stopReason, inputTokens, outputTokens)
        {
            ToolCalls = toolCalls,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
        };
    }
}
