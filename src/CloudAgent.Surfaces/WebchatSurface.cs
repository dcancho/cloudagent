using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
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
/// </summary>
public sealed class WebchatSurface(int port = 5080) : ISurface
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();

    public string Name => "webchat";

    public string Url => $"http://localhost:{port}/";

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
            _sockets[conversationId] = socket;

            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (received.MessageType == WebSocketMessageType.Close)
                        break;

                    var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await onMessage(
                            new InboundMessage("webchat", conversationId, conversationId, "web", text, DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _sockets.TryRemove(conversationId, out _);
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

    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (_sockets.TryGetValue(message.ConversationId, out var socket) && socket.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(message.Text);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
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
            .you span, .agent span { display: inline-block; padding: .4rem .7rem; border-radius: 12px; }
            .you span { background: #2563eb; color: #fff; }
            .agent span { background: #f1f1f1; }
            #row { display: flex; gap: .5rem; margin-top: .75rem; }
            #text { flex: 1; padding: .5rem; }
          </style>
        </head>
        <body>
          <h2>CloudAgent</h2>
          <div id="log"></div>
          <div id="row">
            <input id="text" placeholder="Message the agent…" autofocus />
            <button onclick="send()">Send</button>
          </div>
          <script>
            const log = document.getElementById('log');
            const input = document.getElementById('text');
            const ws = new WebSocket(`ws://${location.host}/ws`);
            function add(cls, t) {
              const d = document.createElement('div');
              d.className = 'msg ' + cls;
              const s = document.createElement('span');
              s.textContent = t; d.appendChild(s);
              log.appendChild(d); log.scrollTop = log.scrollHeight;
            }
            ws.onmessage = e => add('agent', e.data);
            function send() {
              const t = input.value.trim();
              if (!t) return;
              add('you', t); ws.send(t); input.value = '';
            }
            input.addEventListener('keydown', e => { if (e.key === 'Enter') send(); });
          </script>
        </body>
        </html>
        """;
}
