using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using TerminalHost.Core;
using TerminalHost.Infrastructure;

namespace TerminalHost.Api;

public sealed class TerminalWebSocketServer : IAsyncDisposable
{
    private readonly TerminalSessionManager _manager;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<Guid, WebSocketClientConnection> _clients = new();
    private WebApplication? _app;

    public TerminalWebSocketServer(TerminalSessionManager manager, AppSettings settings)
    {
        _manager = manager;
        _settings = settings;
        _manager.OutputReceived += (_, e) => _ = BroadcastAsync(new { type = "output", sessionId = e.SessionId, data = e.Data });
        _manager.SessionExited += (_, e) => _ = BroadcastAsync(new { type = "exited", sessionId = e.SessionId, exitCode = e.ExitCode });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TerminalWebSocketServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>()
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, _settings.ApiPort));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            service = "TerminalHost",
            sessions = _manager.List().Count
        }));

        app.Map("/ws", (HttpContext context) => HandleWebSocketAsync(context));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = context.Request.Query["token"].ToString();
        if (!CryptographicEquals(token, _settings.ApiToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var client = new WebSocketClientConnection(socket);
        _clients[client.Id] = client;

        try
        {
            await client.SendJsonAsync(new
            {
                type = "hello",
                service = "TerminalHost",
                protocol = 1,
                sessions = _manager.List()
            }).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open)
            {
                var text = await client.ReceiveTextAsync(context.RequestAborted).ConfigureAwait(false);
                if (text is null) break;
                await HandleMessageAsync(client, text, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try { await client.SendJsonAsync(new { type = "error", error = ex.Message }); } catch { }
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            await client.DisposeAsync();
        }
    }

    private async Task HandleMessageAsync(WebSocketClientConnection client, string text, CancellationToken cancellationToken)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            requestId = GetString(root, "requestId");
            var action = RequireString(root, "action").ToLowerInvariant();

            switch (action)
            {
                case "create":
                {
                    var info = await _manager.CreateAsync(new TerminalCreateRequest(
                        GetString(root, "shell") ?? "pwsh",
                        GetString(root, "cwd"),
                        GetInt(root, "cols", 120),
                        GetInt(root, "rows", 30))).ConfigureAwait(false);
                    await client.SendJsonAsync(new { type = "created", requestId, session = info }, cancellationToken);
                    _manager.Begin(info.Id);
                    break;
                }
                case "list":
                    await client.SendJsonAsync(new { type = "sessions", requestId, sessions = _manager.List() }, cancellationToken);
                    break;
                case "write":
                {
                    var sessionId = RequireString(root, "sessionId");
                    await _manager.WriteAsync(sessionId, RequireString(root, "data"), cancellationToken).ConfigureAwait(false);
                    await client.SendJsonAsync(new { type = "ok", requestId, action }, cancellationToken);
                    break;
                }
                case "signal":
                {
                    var sessionId = RequireString(root, "sessionId");
                    await _manager.SendSignalAsync(sessionId, RequireString(root, "signal"), cancellationToken).ConfigureAwait(false);
                    await client.SendJsonAsync(new { type = "ok", requestId, action }, cancellationToken);
                    break;
                }
                case "resize":
                {
                    var sessionId = RequireString(root, "sessionId");
                    _manager.Resize(sessionId, GetInt(root, "cols", 120), GetInt(root, "rows", 30));
                    await client.SendJsonAsync(new { type = "ok", requestId, action }, cancellationToken);
                    break;
                }
                case "snapshot":
                {
                    var sessionId = RequireString(root, "sessionId");
                    await client.SendJsonAsync(new
                    {
                        type = "snapshot",
                        requestId,
                        sessionId,
                        data = _manager.GetSnapshot(sessionId)
                    }, cancellationToken);
                    break;
                }
                case "stop":
                {
                    var sessionId = RequireString(root, "sessionId");
                    await _manager.StopAsync(sessionId, GetBool(root, "graceful", true), GetBool(root, "remove", false)).ConfigureAwait(false);
                    await client.SendJsonAsync(new { type = "ok", requestId, action }, cancellationToken);
                    break;
                }
                case "ping":
                    await client.SendJsonAsync(new { type = "pong", requestId, time = DateTimeOffset.UtcNow }, cancellationToken);
                    break;
                default:
                    throw new ArgumentException($"未知 action：{action}");
            }
        }
        catch (Exception ex)
        {
            await client.SendJsonAsync(new { type = "error", requestId, error = ex.Message }, cancellationToken);
        }
    }

    private async Task BroadcastAsync(object message)
    {
        var clients = _clients.Values.ToArray();
        foreach (var client in clients)
        {
            try { await client.SendJsonAsync(message).ConfigureAwait(false); }
            catch { _clients.TryRemove(client.Id, out _); }
        }
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
        var b = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string RequireString(JsonElement root, string name)
        => GetString(root, name) ?? throw new ArgumentException($"缺少字段：{name}");

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool GetBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();

        if (_app is not null)
        {
            try { await _app.StopAsync(); } catch { }
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
