using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public sealed class RestartRecoveryTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _routers = [];
    private readonly CancellationTokenSource _run = new();

    public void Dispose()
    {
        _run.Cancel();
        foreach (var router in _routers)
            router.Dispose();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static InboundMessage Direct(string text, DateTimeOffset? at = null) =>
        new("chat", "room", ConversationKind.Direct, "ana", "Ana", text, at ?? DateTimeOffset.UtcNow);

    private InboxStore Inbox(TimeProvider? time = null) => new(_directory, time);

    private (ConversationRouter Router, FakeSurface Surface, GatedModel Model) Start(
        InboxStore inbox, bool gated = false, TimeProvider? time = null, FakeSurface? surface = null,
        RedeliveryOptions? redelivery = null)
    {
        surface ??= new FakeSurface();
        var model = new GatedModel(gated);
        var router = new ConversationRouter(
            [surface],
            _ =>
            {
                var queue = new QueueInputSource();
                var loop = new AgentLoop(model, new AgentOptions { IterationDelay = TimeSpan.Zero }, queue);
                return new AgentConversation(queue, loop);
            },
            turnTaking: new TurnTakingOptions { ImmediateSurfaces = new HashSet<string> { "chat" } },
            inbox: inbox,
            redelivery: redelivery,
            time: time);
        _routers.Add(router);
        _ = router.RunAsync(_run.Token);
        return (router, surface, model);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition was never met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_message_is_written_down_before_the_agent_reads_it_and_struck_off_after_its_turn()
    {
        var inbox = Inbox();
        var (_, surface, model) = Start(inbox, gated: true);

        await surface.ReceiveAsync(Direct("hola"));
        await Eventually(() => model.Calls == 1);

        Assert.Equal("hola", Assert.Single(inbox.Pending()).Message.Text);

        model.Release();
        await Eventually(() => inbox.Pending().Count == 0);
        Assert.Equal(["ok"], surface.Sent);
    }

    [Fact]
    public async Task Drain_waits_for_a_running_turn_and_its_reply()
    {
        var inbox = Inbox();
        var (router, surface, model) = Start(inbox, gated: true);
        await surface.ReceiveAsync(Direct("hola"));
        await Eventually(() => model.Calls == 1);

        var drain = router.DrainAsync(Patience);
        await Task.Delay(150);
        Assert.False(drain.IsCompleted);

        model.Release();
        await drain.WaitAsync(Patience);

        Assert.Equal(["ok"], surface.Sent);
        Assert.Empty(inbox.Pending());
    }

    [Fact]
    public async Task Drain_gives_up_at_its_limit_and_leaves_the_message_in_the_inbox()
    {
        var inbox = Inbox();
        var (router, surface, model) = Start(inbox, gated: true);
        await surface.ReceiveAsync(Direct("hola"));
        await Eventually(() => model.Calls == 1);

        await router.DrainAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Patience);

        Assert.Equal("hola", Assert.Single(inbox.Pending()).Message.Text);
    }

    [Fact]
    public async Task Drain_hands_over_a_burst_still_waiting_for_its_end_of_turn()
    {
        var inbox = Inbox();
        var (router, surface, model) = Start(inbox, surface: new FakeSurface("slow"));
        await surface.ReceiveAsync(Direct("hola") with { Surface = "slow" });

        await router.DrainAsync(Patience).WaitAsync(Patience);

        Assert.Equal(1, model.Calls);
        Assert.Equal(["ok"], surface.Sent);
        Assert.Empty(inbox.Pending());
    }

    [Fact]
    public async Task A_message_that_arrives_during_a_drain_is_kept_but_not_started()
    {
        var inbox = Inbox();
        var (router, surface, model) = Start(inbox, gated: true);
        await surface.ReceiveAsync(Direct("primero"));
        await Eventually(() => model.Calls == 1);
        var drain = router.DrainAsync(Patience);

        await surface.ReceiveAsync(Direct("segundo"));
        model.Release();
        await drain.WaitAsync(Patience);

        Assert.Equal("segundo", Assert.Single(inbox.Pending()).Message.Text);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Unfinished_messages_are_redelivered_to_a_new_router_after_one_notice()
    {
        var inbox = Inbox();
        inbox.Add(Direct("uno"));
        inbox.Add(Direct("dos"));

        var (_, surface, model) = Start(Inbox());

        await Eventually(() => inbox.Pending().Count == 0 && surface.Sent.Count == 2);
        Assert.Equal(["Me reinicié mientras procesaba tu mensaje. Lo retomo ahora.", "ok"], surface.Sent);
        Assert.Equal("uno\ndos", model.LastUserText);
    }

    [Fact]
    public async Task A_redelivered_group_message_keeps_its_speaker_and_the_context_overheard_before_it()
    {
        var inbox = Inbox();
        var overheard = new InboundMessage("chat", "room", ConversationKind.Group, "bo", "Bo", "llueve", DateTimeOffset.UtcNow, false);
        inbox.Add(new InboundMessage("chat", "room", ConversationKind.Group, "ana", "Ana", "¿abrigo?", DateTimeOffset.UtcNow, true, [overheard]));

        var (_, _, model) = Start(Inbox());

        await Eventually(() => model.Calls == 1);
        Assert.Equal("[overheard] Bo: llueve\nAna: ¿abrigo?", model.LastUserText);
    }

    [Fact]
    public async Task A_message_is_redelivered_at_most_twice()
    {
        var inbox = Inbox();
        var id = inbox.Add(Direct("hola"));
        inbox.RecordAttempt([id]);

        var (_, surface, model) = Start(Inbox(), gated: true);
        await Eventually(() => model.Calls == 1);
        Assert.Equal(2, Assert.Single(inbox.Pending()).Attempts);

        var (_, other, otherModel) = Start(Inbox());
        await Task.Delay(300);

        Assert.Equal(0, otherModel.Calls);
        Assert.Empty(other.Sent);
        Assert.Empty(inbox.Pending());
    }

    [Fact]
    public async Task A_message_older_than_the_limit_is_dropped_with_a_single_different_notice()
    {
        var start = DateTimeOffset.UnixEpoch.AddDays(1);
        var time = new ManualTime(start);
        var inbox = Inbox(time);
        inbox.Add(Direct("uno", start));
        inbox.Add(Direct("dos", start));
        time.Advance(TimeSpan.FromMinutes(31));

        var (_, surface, model) = Start(Inbox(time), time: time);

        await Eventually(() => inbox.Pending().Count == 0);
        await Eventually(() => surface.Sent.Count == 1);
        Assert.StartsWith("Mi reinicio fue hace un rato", surface.Sent[0]);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Redelivery_waits_until_the_surface_can_send()
    {
        var inbox = Inbox();
        inbox.Add(Direct("hola"));
        var surface = new FakeSurface { Ready = new TaskCompletionSource() };

        var (_, _, model) = Start(Inbox(), surface: surface);
        await Task.Delay(300);

        Assert.Empty(surface.Sent);
        Assert.Equal(0, model.Calls);

        surface.Ready.SetResult();
        await Eventually(() => inbox.Pending().Count == 0);
        Assert.Equal(2, surface.Sent.Count);
    }

    [Fact]
    public async Task Closing_a_conversation_forgets_its_unfinished_messages()
    {
        var inbox = Inbox();
        var (router, surface, model) = Start(inbox, gated: true);
        await surface.ReceiveAsync(Direct("hola"));
        await Eventually(() => model.Calls == 1);

        await router.CloseAsync("chat", "room");

        Assert.Empty(inbox.Pending());
    }

    private sealed class FakeSurface(string name = "chat") : IReadinessSource
    {
        private readonly List<string> _sent = [];
        private Func<InboundMessage, CancellationToken, Task>? _handler;

        public string Name => name;

        public TaskCompletionSource? Ready { get; init; }

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                    return _sent.ToList();
            }
        }

        public async Task StartAsync(Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
        {
            _handler = onMessage;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public Task ReceiveAsync(InboundMessage message)
        {
            while (_handler is null)
                Thread.Sleep(5);
            return _handler(message with { Surface = name }, CancellationToken.None);
        }

        public Task WaitUntilReadyAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Ready?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sent)
                _sent.Add(message.Text);
            return Task.CompletedTask;
        }
    }

    private sealed class GatedModel(bool gated) : ILlmProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string Name => "gated";

        public int Calls => Volatile.Read(ref _calls);

        public string? LastUserText { get; private set; }

        public void Release() => _gate.TrySetResult();

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            LastUserText = request.Messages.LastOrDefault(m => m.Role == Role.User)?.Content;
            Interlocked.Increment(ref _calls);
            if (gated)
                await _gate.Task.WaitAsync(cancellationToken);
            return new LlmResponse("ok", InputTokens: 1, OutputTokens: 1);
        }
    }
}
