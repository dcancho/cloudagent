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
/// never say hello keep receiving plain text.
/// </summary>
public sealed class WebchatSurface(int port = 5080) : ITypingSource, ITypingIndicator
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
            var conversationId = Guid.NewGuid().ToString("N")[..8];
            var client = new Client(socket);
            _clients[conversationId] = client;

            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var frame = await ReceiveFrameAsync(socket, cancellationToken);
                    if (frame is null)
                        break;

                    var text = HandleFrame(client, conversationId, frame);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await onMessage(
                            new InboundMessage("webchat", conversationId, conversationId, "web", text, DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                _clients.TryRemove(conversationId, out _);
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
                return frame;
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
          <h2>CloudAgent</h2>
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

            ws.onopen = () => ws.send(JSON.stringify({ type: 'hello', protocol: 2 }));
            ws.onmessage = e => {
              let frame;
              try { frame = JSON.parse(e.data); } catch { frame = null; }
              if (frame && frame.type === 'typing') { showTyping(); return; }
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
