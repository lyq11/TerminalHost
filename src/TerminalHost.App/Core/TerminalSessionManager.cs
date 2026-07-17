using System.Collections.Concurrent;
using System.Text;
using TerminalHost.Core.ConPty;

namespace TerminalHost.Core;

public sealed class TerminalSessionManager : IAsyncDisposable
{
    private sealed class SessionEntry
    {
        private const int MaxSnapshotChars = 1_000_000;
        private readonly object _bufferLock = new();
        private readonly StringBuilder _buffer = new();

        internal TerminalSessionInfo Info { get; set; } = null!;
        internal ConPtySession Session { get; init; } = null!;

        internal void Append(string text)
        {
            lock (_bufferLock)
            {
                _buffer.Append(text);
                if (_buffer.Length > MaxSnapshotChars)
                    _buffer.Remove(0, _buffer.Length - (MaxSnapshotChars * 3 / 4));
            }
        }

        internal string Snapshot()
        {
            lock (_bufferLock) return _buffer.ToString();
        }
    }

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TerminalOutputEvent>? OutputReceived;
    public event EventHandler<TerminalExitEvent>? SessionExited;

    public async Task<TerminalSessionInfo> CreateAsync(TerminalCreateRequest request)
    {
        var shell = ShellCommand.Resolve(request.Shell);
        var workingDirectory = NormalizeWorkingDirectory(request.WorkingDirectory);
        var columns = Math.Clamp(request.Columns, 20, 500);
        var rows = Math.Clamp(request.Rows, 5, 300);
        var id = Guid.NewGuid().ToString("N");

        var conPty = ConPtySession.CreatePaused(shell.CommandLine, workingDirectory, columns, rows);
        var info = new TerminalSessionInfo(
            id,
            shell.Shell,
            shell.CommandLine,
            workingDirectory,
            columns,
            rows,
            DateTimeOffset.Now,
            true);

        var entry = new SessionEntry { Info = info, Session = conPty };
        if (!_sessions.TryAdd(id, entry))
        {
            await conPty.DisposeAsync();
            throw new InvalidOperationException("无法注册终端会话。" );
        }

        conPty.Output += data =>
        {
            entry.Append(data);
            OutputReceived?.Invoke(this, new TerminalOutputEvent(id, data));
        };
        conPty.Exited += code =>
        {
            entry.Info = entry.Info with { IsRunning = false };
            SessionExited?.Invoke(this, new TerminalExitEvent(id, code));
        };

        return info;
    }

    public void Begin(string sessionId)
        => GetEntry(sessionId).Session.Begin();

    public IReadOnlyList<TerminalSessionInfo> List()
        => _sessions.Values.Select(x => x.Info).OrderBy(x => x.StartedAt).ToArray();

    public TerminalSessionInfo Get(string sessionId)
        => GetEntry(sessionId).Info;

    public string GetSnapshot(string sessionId)
        => GetEntry(sessionId).Snapshot();

    public Task WriteAsync(string sessionId, string data, CancellationToken cancellationToken = default)
        => GetEntry(sessionId).Session.WriteAsync(data, cancellationToken);

    public Task SendSignalAsync(string sessionId, string signal, CancellationToken cancellationToken = default)
    {
        var data = signal.Trim().ToLowerInvariant() switch
        {
            "ctrlc" or "ctrl+c" => "\x03",
            "ctrld" or "ctrl+d" => "\x04",
            "escape" or "esc" => "\x1b",
            "enter" => "\r",
            _ => throw new ArgumentException("支持的 signal：ctrlC、ctrlD、escape、enter。", nameof(signal))
        };
        return WriteAsync(sessionId, data, cancellationToken);
    }

    public void Resize(string sessionId, int columns, int rows)
    {
        columns = Math.Clamp(columns, 20, 500);
        rows = Math.Clamp(rows, 5, 300);
        var entry = GetEntry(sessionId);
        entry.Session.Resize(columns, rows);
        entry.Info = entry.Info with { Columns = columns, Rows = rows };
    }

    public async Task StopAsync(string sessionId, bool graceful = true, bool remove = false)
    {
        var entry = GetEntry(sessionId);
        await entry.Session.StopAsync(graceful).ConfigureAwait(false);
        entry.Info = entry.Info with { IsRunning = false };
        if (remove && _sessions.TryRemove(sessionId, out var removed))
            await removed.Session.DisposeAsync();
    }

    public async Task RemoveAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
            await entry.Session.DisposeAsync();
    }

    private SessionEntry GetEntry(string sessionId)
        => _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"终端会话不存在：{sessionId}");

    private static string NormalizeWorkingDirectory(string? path)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        candidate = Path.GetFullPath(candidate);
        if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException($"工作目录不存在：{candidate}");
        return candidate;
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.ToArray();
        _sessions.Clear();
        foreach (var pair in sessions)
            await pair.Value.Session.DisposeAsync();
    }
}
