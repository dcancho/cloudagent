using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// Telegram via the Bot API using long polling (getUpdates) — no public URL, no SDK, just HTTP.
/// The conversation id is the Telegram chat id, plus the topic id for a forum topic in a supergroup
/// (<c>chatId#topicId</c>), so each topic is its own conversation. A message that mentions the bot or
/// replies to one of its messages is marked as addressed to it, and the bot's @username reaches the
/// agent as the agent's name. With the bot's privacy mode on (BotFather's default) a group only
/// delivers those messages and commands; turn it off for the agent to follow the rest as context.
/// </summary>
public sealed class TelegramSurface : ITypingIndicator
{
    private const char TopicSeparator = '#';

    private static readonly JsonSerializerOptions SkipNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _agentName;
    private TelegramBot? _bot;

    /// <param name="botToken">The bot token.</param>
    /// <param name="http">The client for the Bot API; a new one when null.</param>
    /// <param name="agentName">How the bot's @username reads to the agent; left as is when null.</param>
    public TelegramSurface(string botToken, HttpClient? http = null, string? agentName = null)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            throw new ArgumentException("A Telegram bot token is required.", nameof(botToken));

        _http = http ?? new HttpClient();
        _baseUrl = $"https://api.telegram.org/bot{botToken}";
        _agentName = agentName;
    }

    public string Name => "telegram";

    public async Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        long offset = 0;
        _bot = await ReadBotAsync(cancellationToken).ConfigureAwait(false);

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

                    var (conversationId, kind) = ReadConversation(msg);
                    var (senderId, senderName) = ReadSender(msg);

                    var text = textEl.GetString() ?? string.Empty;
                    var addressing = _bot is null ? Addressing.None : ReadAddressing(msg, text, _bot);
                    if (_bot is not null && _agentName is not null)
                        text = NameTheBot(text, _bot.Username, _agentName);

                    await onMessage(
                        new InboundMessage("telegram", conversationId, kind, senderId, senderName,
                            text, DateTimeOffset.UtcNow, addressing),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var (chatId, topicId) = SplitConversation(message.ConversationId);
        var payload = new { chat_id = chatId, message_thread_id = topicId, text = message.Text };
        using var response = await _http
            .PostAsJsonAsync($"{_baseUrl}/sendMessage", payload, SkipNulls, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendTypingAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var (chatId, topicId) = SplitConversation(conversationId);
        var payload = new { chat_id = chatId, message_thread_id = topicId, action = "typing" };
        using var response = await _http
            .PostAsJsonAsync($"{_baseUrl}/sendChatAction", payload, SkipNulls, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The conversation a Telegram message belongs to: its chat, narrowed to the forum topic when it
    /// was posted in one. Private chats are direct; groups and supergroups are shared.
    /// </summary>
    public static (string ConversationId, ConversationKind Kind) ReadConversation(JsonElement message)
    {
        var chat = message.GetProperty("chat");
        var chatId = chat.GetProperty("id").GetInt64().ToString();
        var kind = chat.TryGetProperty("type", out var type) && type.GetString() == "private"
            ? ConversationKind.Direct
            : ConversationKind.Group;

        if (message.TryGetProperty("is_topic_message", out var topic) && topic.ValueKind == JsonValueKind.True
            && message.TryGetProperty("message_thread_id", out var thread))
            return ($"{chatId}{TopicSeparator}{thread.GetInt64()}", kind);

        return (chatId, kind);
    }

    /// <summary>The bot's own account, as Telegram reports it.</summary>
    public sealed record TelegramBot(long Id, string Username);

    /// <summary>
    /// Whether a message is addressed to the bot: it mentions its @username, or replies to one of its
    /// messages. In a forum topic every message replies to the topic's first message, which only counts
    /// when the bot wrote it as a reply of its own.
    /// </summary>
    public static Addressing ReadAddressing(JsonElement message, string text, TelegramBot bot)
    {
        if (MentionOf(bot.Username).IsMatch(text))
            return Addressing.Explicit;

        if (message.TryGetProperty("reply_to_message", out var replied)
            && replied.TryGetProperty("from", out var from)
            && from.TryGetProperty("id", out var fromId) && fromId.GetInt64() == bot.Id)
        {
            var topicStarter = message.TryGetProperty("message_thread_id", out var thread)
                && replied.TryGetProperty("message_id", out var repliedId)
                && repliedId.GetInt64() == thread.GetInt64();
            return topicStarter ? Addressing.None : Addressing.Explicit;
        }

        return Addressing.None;
    }

    /// <summary>The text with the bot's @username replaced by the agent's name.</summary>
    public static string NameTheBot(string text, string botUsername, string agentName) =>
        MentionOf(botUsername).Replace(text, "@" + agentName);

    private static Regex MentionOf(string username) => new(
        $@"@{Regex.Escape(username)}(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task<TelegramBot?> ReadBotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/getMe", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var result = document.RootElement.GetProperty("result");
            return new TelegramBot(result.GetProperty("id").GetInt64(), result.GetProperty("username").GetString()!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Without its own account the bot cannot tell mentions apart; it still runs.
            Console.Error.WriteLine($"  [telegram] could not read the bot's account: {ex.Message}");
            return null;
        }
    }

    private static (string ChatId, long? TopicId) SplitConversation(string conversationId)
    {
        var separator = conversationId.IndexOf(TopicSeparator);
        return separator < 0
            ? (conversationId, null)
            : (conversationId[..separator], long.Parse(conversationId[(separator + 1)..]));
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
