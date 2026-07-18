namespace TerminalHost.Core;

public sealed record TerminalCreateRequest(
    string Shell,
    string? WorkingDirectory,
    int Columns = 120,
    int Rows = 30,
    bool CreatedByUi = false);

public sealed record TerminalSessionInfo(
    string Id,
    string Shell,
    string CommandLine,
    string WorkingDirectory,
    int Columns,
    int Rows,
    DateTimeOffset StartedAt,
    bool IsRunning);

public sealed record TerminalOutputEvent(string SessionId, string Data);
public sealed record TerminalExitEvent(string SessionId, uint ExitCode);
public sealed record TerminalCreatedEvent(TerminalSessionInfo Session, bool CreatedByUi);
