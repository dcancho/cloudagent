using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Surfaces;
using Discord;
using Discord.WebSocket;

namespace CloudAgent.Surfaces;

/// <summary>
/// Discord via Discord.Net's gateway client. Each channel, thread or DM is a conversation; only a
/// one-to-one DM is direct. Requires a bot token and the Message Content intent enabled in the
/// Discord developer portal. A message that mentions the bot or replies to one of its messages is
/// marked as addressed to it, and mentions reach the agent as names: the bot's as the agent's name.
/// </summary>
public sealed class DiscordSurface : ITypingSource, ITypingIndicator
{
    private static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(10);

    private readonly DiscordSocketClient _client;
    private readonly string _token;
    private readonly string? _agentName;
    private Func<InboundMessage, CancellationToken, Task>? _onMessage;

    /// <param name="token">The bot token.</param>
    /// <param name="agentName">How the bot's own mentions read to the agent; its Discord username when null.</param>
    public DiscordSurface(string token, string? agentName = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A Discord bot token is required.", nameof(token));

        _token = token;
        _agentName = agentName;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                | GatewayIntents.GuildMessages
                | GatewayIntents.DirectMessages
                | GatewayIntents.MessageContent
                | GatewayIntents.GuildMessageTyping
                | GatewayIntents.DirectMessageTyping,
            LogLevel = LogSeverity.Info,
        });
    }

    public string Name => "discord";

    public event Action<InboundTyping>? TypingReceived;

    public async Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        _onMessage = onMessage;
        _client.Log += message =>
        {
            Console.WriteLine($"  [discord] {message}");
            return Task.CompletedTask;
        };
        _client.MessageReceived += HandleMessageAsync;
        _client.UserIsTyping += HandleTypingAsync;

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) { }

        await _client.StopAsync();
    }

    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (ulong.TryParse(message.ConversationId, out var channelId) &&
            await _client.GetChannelAsync(channelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync(message.Text);
        }
    }

    public async Task SendTypingAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (ulong.TryParse(conversationId, out var channelId) &&
            await _client.GetChannelAsync(channelId) is IMessageChannel channel)
        {
            await channel.TriggerTypingAsync();
        }
    }

    private Task HandleTypingAsync(Cacheable<IUser, ulong> user, Cacheable<IMessageChannel, ulong> channel)
    {
        if (user.Id != _client.CurrentUser?.Id)
        {
            TypingReceived?.Invoke(new InboundTyping(
                "discord", channel.Id.ToString(), user.Id.ToString(), DateTimeOffset.UtcNow + TypingLifetime));
        }
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(SocketMessage message)
    {
        // Ignore our own and other bots' messages.
        if (message.Author.IsBot || _onMessage is null)
            return;

        var self = _client.CurrentUser;
        var addressed = self is not null
            && (message.MentionedUsers.Any(u => u.Id == self.Id)
                || (message is SocketUserMessage { ReferencedMessage.Author.Id: var repliedTo } && repliedTo == self.Id));

        await _onMessage(
            new InboundMessage(
                "discord",
                message.Channel.Id.ToString(),
                message.Channel is IDMChannel ? ConversationKind.Direct : ConversationKind.Group,
                message.Author.Id.ToString(),
                message.Author.Username,
                ReadableText(message, self),
                DateTimeOffset.UtcNow,
                addressed ? Addressing.Explicit : Addressing.None),
            CancellationToken.None);
    }

    /// <summary>The message with mentions as names, the bot's own as the agent's name.</summary>
    private string ReadableText(SocketMessage message, SocketSelfUser? self)
    {
        var text = message.Content;
        if (self is not null)
        {
            var name = "@" + (_agentName ?? self.Username);
            text = text.Replace($"<@{self.Id}>", name).Replace($"<@!{self.Id}>", name);
        }
        foreach (var user in message.MentionedUsers)
        {
            var name = "@" + ((user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username);
            text = text.Replace($"<@{user.Id}>", name).Replace($"<@!{user.Id}>", name);
        }
        return text;
    }
}
