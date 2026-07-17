using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TerminalHost.Api;

internal sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    internal Guid Id { get; } = Guid.NewGuid();
    internal WebSocket Socket { get; }

    internal WebSocketClientConnection(WebSocket socket) => Socket = socket;

    internal async Task SendJsonAsync(object value, CancellationToken cancellationToken = default)
    {
        if (Socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("只支持文本 JSON WebSocket 消息。" );
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidOperationException("消息过大。" );
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
        catch { }
        Socket.Dispose();
        _sendLock.Dispose();
    }
}
