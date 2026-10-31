using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Surfaces;
using CloudAgent.Core.Tests.Support;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public class GroupRoutingTests
{
    private sealed class FakeSurface : ISurface
    {
        private Func<InboundMessage, CancellationToken, Task>? _onMessage;

        public string Name => "fake";

        public List<string> Sent { get; } = [];

        public Task StartAsync(Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
        {
            _onMessage = onMessage;
            return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { });
        }

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            lock (Sent)
                Sent.Add(message.Text);
            return Task.CompletedTask;
        }

        public Task Say(string senderId, string text, Addressing addressing = Addressing.None) =>
            _onMessage!(new InboundMessage("fake", "room", ConversationKind.Group, senderId, null, text,
                DateTimeOffset.UtcNow, addressing), CancellationToken.None);
    }

    private static (ConversationRouter Router, FakeSurface Surface, CancellationTokenSource Run) Start(ScriptedLlmProvider provider)
    {
        var surface = new FakeSurface();
        var router = new ConversationRouter(
            [surface],
            _ =>
            {
                var queue = new QueueInputSource();
                return new AgentConversation(queue, new AgentLoop(provider, new AgentOptions { IterationDelay = TimeSpan.Zero }, queue));
            },
            turnTaking: new TurnTakingOptions { ImmediateSurfaces = new HashSet<string> { "fake" } },
            groups: new GroupAttention(new AgentIdentity("Atlas")));
        var run = new CancellationTokenSource();
        _ = router.RunAsync(run.Token);
        return (router, surface, run);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static string LastUser(LlmRequest request) => request.Messages.Last(m => m.Role == Role.User).Content!;

    [Fact]
    public async Task Chatter_is_held_and_reaches_the_agent_with_the_next_mention()
    {
        var provider = new ScriptedLlmProvider().Otherwise("Hola a todos");
        var (router, surface, run) = Start(provider);
        using (router)
        using (run)
        {
            await Task.Delay(50);
            await surface.Say("1", "¿vamos al cine?");
            await surface.Say("2", "sí, a las 8");
            await Task.Delay(100);
            Assert.Empty(provider.Requests);

            await surface.Say("1", "Atlas, ¿qué dan hoy?");
            await Until(() => surface.Sent.Count == 1);

            var text = LastUser(Assert.Single(provider.Requests));
            Assert.Equal(
                "[Earlier in the group, not addressed to you]\n1: ¿vamos al cine?\n2: sí, a las 8\n\n[New]\n1: Atlas, ¿qué dan hoy?",
                text);
            Assert.Equal("Hola a todos", surface.Sent[0]);
        }
    }

    [Fact]
    public async Task Silent_reply_sends_nothing()
    {
        var provider = new ScriptedLlmProvider().Otherwise(SilentReply.Marker);
        var (router, surface, run) = Start(provider);
        using (router)
        using (run)
        {
            await Task.Delay(50);
            await surface.Say("1", "Atlas se equivocó ayer");
            await Until(() => provider.Requests.Count == 1);
            await Task.Delay(100);

            Assert.Empty(surface.Sent);
        }
    }
}
