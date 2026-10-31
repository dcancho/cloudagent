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
/// Discord developer portal.
/// </summary>
public sealed class DiscordSurface : ITypingSource, ITypingIndicator
{
    private static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(10);

    private readonly DiscordSocketClient _client;
    private readonly string _token;
    private Func<InboundMessage, CancellationToken, Task>? _onMessage;

    public DiscordSurface(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A Discord bot token is required.", nameof(token));

        _token = token;
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

        await _onMessage(
            new InboundMessage(
                "discord",
                message.Channel.Id.ToString(),
                message.Channel is IDMChannel ? ConversationKind.Direct : ConversationKind.Group,
                message.Author.Id.ToString(),
                message.Author.Username,
                message.CleanContent,
                DateTimeOffset.UtcNow,
                IsAddressedToMe(message)),
            CancellationToken.None);
    }

    private bool IsAddressedToMe(SocketMessage message)
    {
        var me = _client.CurrentUser;
        if (me is null)
            return true;

        if (message.MentionedUsers.Any(u => u.Id == me.Id))
            return true;

        if (message is SocketUserMessage { ReferencedMessage: { } referenced } && referenced.Author.Id == me.Id)
            return true;

        return message.Channel is SocketGuildChannel guildChannel
            && guildChannel.Guild.CurrentUser.Roles.Any(role => message.MentionedRoles.Any(m => m.Id == role.Id) && !role.IsEveryone);
    }
}
