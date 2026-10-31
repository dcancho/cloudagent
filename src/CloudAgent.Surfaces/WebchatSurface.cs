using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Surfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudAgent.Surfaces;

/// <summary>
/// A self-hosted web chat: a tiny Kestrel server serves a chat page at "/" and a WebSocket at
/// "/ws". Each browser connection is its own conversation. No third-party account — open the
/// page and talk to the agent. Outbound replies are pushed to the matching socket.
///
/// Protocol: a plain text frame is a message. A client that sends <c>{"type":"hello","protocol":2}</c>
/// switches to typed JSON frames (<c>message</c> and <c>typing</c>) in both directions; clients that
/// never say hello keep receiving plain text. A hello may carry <c>"conversation"</c> to continue an
/// earlier conversation; the server answers with <c>welcome</c> (the conversation id to keep) and, when
/// there is one, <c>history</c> (the messages and tool activity so far). A <c>list</c> frame is answered with
/// <c>conversations</c>: every web chat conversation, most recent first. A <c>delete</c> frame naming a
/// <c>conversation</c> deletes it and is answered with <c>conversations</c>. A hello that starts a new
/// conversation may name a <c>workspace</c> for it to work in; <c>welcome</c> carries the conversation's
/// workspace and the ones available, and each listed conversation carries its own. While the agent works,
/// each of its tool calls arrives as an <c>activity</c> frame: the tool, whether it succeeded, a summary,
/// and for an edit the replaced and the new text.
/// </summary>
/// <param name="port">Port to listen on, on localhost.</param>
/// <param name="history">The messages and tool activity so far in a conversation, by conversation id.</param>
/// <param name="conversations">Every web chat conversation, most recent first.</param>
/// <param name="delete">Deletes a conversation by id.</param>
/// <param name="workspaces">The workspaces a conversation can work in, and which one each uses.</param>
public sealed class WebchatSurface(
    int port = 5080,
    Func<string, IReadOnlyList<ConversationEntry>>? history = null,
    Func<IReadOnlyList<ConversationSummary>>? conversations = null,
    Func<string, Task<bool>>? delete = null,
    WebchatWorkspaces? workspaces = null) : ITypingSource, ITypingIndicator, IActivityIndicator
{
    private static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(4);

    private readonly ConcurrentDictionary<string, Client> _clients = new();

    public string Name => "webchat";

    public string Url => $"http://localhost:{port}/";

    public event Action<InboundTyping>? TypingReceived;

    public async Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new Client(socket);
            string? conversationId = null;

            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var frame = await ReceiveFrameAsync(socket, cancellationToken);
                    if (frame is null)
                        break;

                    // The first frame binds the connection to a conversation: the one a hello names, or a new one.
                    if (conversationId is null)
                    {
                        var (id, consumed) = Bind(client, frame);
                        conversationId = id;
                        _clients[conversationId] = client;
                        if (client.Typed)
                            await WelcomeAsync(client, conversationId, cancellationToken);
                        if (consumed)
                            continue;
                    }

                    if (client.Typed && TypeOf(frame) == "list")
                    {
                        await SendConversationsAsync(client, cancellationToken);
                        continue;
                    }

                    if (client.Typed && TypeOf(frame) == "delete")
                    {
                        await DeleteAsync(client, frame, cancellationToken);
                        continue;
                    }

                    var text = HandleFrame(client, conversationId, frame);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await onMessage(
                            new InboundMessage("webchat", conversationId, ConversationKind.Direct, conversationId, "web", text, DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                // A newer connection to the same conversation may have taken its place; leave that one.
                if (conversationId is not null)
                    _clients.TryRemove(new KeyValuePair<string, Client>(conversationId, client));
            }
        });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[webchat] failed to start: {ex.Message}");
            throw;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) { }
        await app.StopAsync(CancellationToken.None);
    }

    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(message.ConversationId, out var client))
            return Task.CompletedTask;

        var frame = client.Typed
            ? new JsonObject { ["type"] = "message", ["text"] = message.Text }.ToJsonString()
            : message.Text;
        return client.SendAsync(frame, cancellationToken);
    }

    public Task SendTypingAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(conversationId, out var client) || !client.Typed)
            return Task.CompletedTask;

        return client.SendAsync(new JsonObject { ["type"] = "typing" }.ToJsonString(), cancellationToken);
    }

    public Task SendActivityAsync(string conversationId, ToolActivity activity, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(conversationId, out var client) || !client.Typed)
            return Task.CompletedTask;

        return client.SendAsync(ActivityJson(activity, "type", "activity").ToJsonString(), cancellationToken);
    }

    private static JsonObject ActivityJson(ToolActivity activity, string kindKey, string kind) => new()
    {
        [kindKey] = kind,
        ["tool"] = activity.Tool,
        ["ok"] = activity.Success,
        ["summary"] = activity.Summary,
        ["path"] = activity.Path,
        ["before"] = activity.Before,
        ["after"] = activity.After,
    };

    /// <summary>
    /// The conversation a connection belongs to, and whether its first frame was only a hello. A hello
    /// may name an existing conversation to continue it; anything else starts a new one.
    /// </summary>
    private (string Id, bool Consumed) Bind(Client client, string frame)
    {
        if (frame.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(frame) is JsonObject json && json["type"]?.GetValue<string>() == "hello")
                {
                    client.Typed = true;
                    var requested = json["conversation"]?.GetValue<string>();
                    if (requested is not null && IsValidId(requested))
                        return (requested, true);

                    var id = NewId();
                    var workspace = json["workspace"]?.GetValue<string>();
                    if (workspaces is not null && workspace is not null && workspaces.Names.Contains(workspace))
                        workspaces.Bind(id, workspace);
                    return (id, true);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }

        return (NewId(), false);
    }

    private async Task WelcomeAsync(Client client, string conversationId, CancellationToken cancellationToken)
    {
        var welcome = new JsonObject { ["type"] = "welcome", ["conversation"] = conversationId };
        if (workspaces is not null)
        {
            welcome["workspace"] = workspaces.Of(conversationId);
            welcome["workspaces"] = new JsonArray(workspaces.Names.Select(n => (JsonNode)n).ToArray());
        }
        await client.SendAsync(welcome.ToJsonString(), cancellationToken);

        var past = history?.Invoke(conversationId) ?? [];
        if (past.Count == 0)
            return;

        var messages = new JsonArray(past
            .Select(m => m.Activity is { } a
                ? ActivityJson(a, "role", "activity")
                : new JsonObject { ["role"] = m.Role, ["text"] = m.Text })
            .Select(j => (JsonNode)j)
            .ToArray());
        await client.SendAsync(new JsonObject { ["type"] = "history", ["messages"] = messages }.ToJsonString(), cancellationToken);
    }

    private Task SendConversationsAsync(Client client, CancellationToken cancellationToken)
    {
        var items = new JsonArray((conversations?.Invoke() ?? [])
            .Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["updatedAt"] = c.UpdatedAt.ToString("O"),
                ["workspace"] = workspaces?.Of(c.Id),
            })
            .ToArray());
        return client.SendAsync(new JsonObject { ["type"] = "conversations", ["items"] = items }.ToJsonString(), cancellationToken);
    }

    private async Task DeleteAsync(Client client, string frame, CancellationToken cancellationToken)
    {
        var id = (JsonNode.Parse(frame) as JsonObject)?["conversation"]?.GetValue<string>();
        if (delete is not null && id is not null && IsValidId(id))
        {
            try
            {
                await delete(id);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[webchat] could not delete conversation {id}: {ex.Message}");
            }
        }
        await SendConversationsAsync(client, cancellationToken);
    }

    private static string? TypeOf(string frame)
    {
        if (!frame.StartsWith('{'))
            return null;
        try
        {
            return (JsonNode.Parse(frame) as JsonObject)?["type"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static bool IsValidId(string id) =>
        id.Length is >= 8 and <= 32 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>Returns message text to deliver, or null when the frame was a control frame.</summary>
    private string? HandleFrame(Client client, string conversationId, string frame)
    {
        if (!frame.StartsWith('{'))
            return frame;

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(frame) as JsonObject;
        }
        catch (JsonException)
        {
            return frame;
        }

        switch (json?["type"]?.GetValue<string>())
        {
            case "hello":
                client.Typed = true;
                return null;
            case "typing":
                TypingReceived?.Invoke(new InboundTyping(
                    "webchat", conversationId, conversationId, DateTimeOffset.UtcNow + TypingLifetime));
                return null;
            case "message":
                return json["text"]?.GetValue<string>();
            default:
                // A typed client only sends text as a message frame; anything else is a frame this
                // server does not know yet, and must not reach the agent as something the person said.
                return client.Typed ? null : frame;
        }
    }

    private static async Task<string?> ReceiveFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;

            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    private sealed class Client(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public bool Typed { get; set; }

        public async Task SendAsync(string frame, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
                return;

            await _send.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }
    }

    private const string HtmlPage = """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>CloudAgent</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 680px; margin: 2rem auto; }
            #log { border: 1px solid #ccc; border-radius: 8px; padding: 1rem; height: 60vh; overflow-y: auto; }
            .msg { margin: .4rem 0; }
            .you { text-align: right; }
            .you span, .agent span { display: inline-block; padding: .4rem .7rem; border-radius: 12px; white-space: pre-wrap; }
            .you span { background: #2563eb; color: #fff; }
            .agent span { background: #f1f1f1; }
            #typing { color: #6b7280; font-size: .85rem; height: 1.2rem; margin-top: .4rem; }
            #row { display: flex; gap: .5rem; margin-top: .25rem; }
            #text { flex: 1; padding: .5rem; }
          </style>
        </head>
        <body>
          <h2>CloudAgent <button onclick="localStorage.removeItem('cloudagent.conversation'); location.reload()">New conversation</button></h2>
          <div id="log"></div>
          <div id="typing"></div>
          <div id="row">
            <input id="text" placeholder="Message the agent…" autofocus />
            <button onclick="send()">Send</button>
          </div>
          <script>
            const log = document.getElementById('log');
            const input = document.getElementById('text');
            const typing = document.getElementById('typing');
            const ws = new WebSocket(`ws://${location.host}/ws`);
            let typingTimer = null;
            let lastTypingSent = 0;

            function add(cls, t) {
              const d = document.createElement('div');
              d.className = 'msg ' + cls;
              const s = document.createElement('span');
              s.textContent = t; d.appendChild(s);
              log.appendChild(d); log.scrollTop = log.scrollHeight;
            }
            function showTyping() {
              typing.textContent = 'Agent is typing';
              clearTimeout(typingTimer);
              typingTimer = setTimeout(() => typing.textContent = '', 6000);
            }
            function hideTyping() {
              clearTimeout(typingTimer);
              typing.textContent = '';
            }

            ws.onopen = () => ws.send(JSON.stringify({
              type: 'hello', protocol: 2, conversation: localStorage.getItem('cloudagent.conversation') || undefined }));
            ws.onmessage = e => {
              let frame;
              try { frame = JSON.parse(e.data); } catch { frame = null; }
              if (frame && frame.type === 'welcome') { localStorage.setItem('cloudagent.conversation', frame.conversation); return; }
              if (frame && frame.type === 'history') {
                frame.messages.filter(m => m.role !== 'activity').forEach(m => add(m.role === 'user' ? 'you' : 'agent', m.text));
                return;
              }
              if (frame && frame.type === 'typing') { showTyping(); return; }
              if (frame && frame.type === 'activity') return;
              hideTyping();
              add('agent', frame && frame.type === 'message' ? frame.text : e.data);
            };

            function send() {
              const t = input.value.trim();
              if (!t) return;
              add('you', t);
              ws.send(JSON.stringify({ type: 'message', text: t }));
              input.value = '';
              lastTypingSent = 0;
            }
            input.addEventListener('keydown', e => { if (e.key === 'Enter') send(); });
            input.addEventListener('input', () => {
              const now = Date.now();
              if (input.value && now - lastTypingSent > 2000 && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'typing' }));
                lastTypingSent = now;
              }
            });
          </script>
        </body>
        </html>
        """;
}

/// <summary>The workspaces a web chat conversation can work in.</summary>
/// <param name="Names">The workspaces a new conversation can choose.</param>
/// <param name="Of">The workspace a conversation works in, by conversation id, or null for the default.</param>
/// <param name="Bind">Records the workspace a new conversation chose.</param>
public sealed record WebchatWorkspaces(
    IReadOnlyList<string> Names, Func<string, string?> Of, Action<string, string> Bind);
