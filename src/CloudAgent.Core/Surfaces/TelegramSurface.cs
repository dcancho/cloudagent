using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// Telegram via the Bot API using long polling (getUpdates) — no public URL, no SDK, just HTTP.
/// The conversation id is the Telegram chat id.
/// </summary>
public sealed class TelegramSurface : ISurface
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public TelegramSurface(string botToken, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            throw new ArgumentException("A Telegram bot token is required.", nameof(botToken));

        _http = http ?? new HttpClient();
        _baseUrl = $"https://api.telegram.org/bot{botToken}";
    }

    public string Name => "telegram";

    public async Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        long offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            JsonDocument? payload = null;
            try
            {
                using var response = await _http
                    .GetAsync($"{_baseUrl}/getUpdates?timeout=30&offset={offset}", cancellationToken)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                payload = JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // transient network error — back off briefly and retry
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (payload)
            {
                if (!payload.RootElement.TryGetProperty("result", out var updates) ||
                    updates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var update in updates.EnumerateArray())
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;

                    if (!update.TryGetProperty("message", out var msg) ||
                        !msg.TryGetProperty("text", out var textEl))
                    {
                        continue;
                    }

                    var chat = msg.GetProperty("chat");
                    var chatId = chat.GetProperty("id").GetInt64().ToString();
                    var (senderId, senderName) = ReadSender(msg);

                    await onMessage(
                        new InboundMessage("telegram", chatId, senderId, senderName,
                            textEl.GetString() ?? string.Empty, DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var payload = new { chat_id = message.ConversationId, text = message.Text };
        using var response = await _http
            .PostAsJsonAsync($"{_baseUrl}/sendMessage", payload, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static (string Id, string? Name) ReadSender(JsonElement message)
    {
        if (!message.TryGetProperty("from", out var from))
            return ("unknown", null);

        var id = from.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "unknown";
        var name = from.TryGetProperty("username", out var u) ? u.GetString()
            : from.TryGetProperty("first_name", out var f) ? f.GetString()
            : null;
        return (id, name);
    }
}
