using System.Text;

namespace TerminalHost.Core;

internal sealed record ShellCommand(string Shell, string Executable, string Arguments)
{
    public string CommandLine => Quote(Executable) + (string.IsNullOrWhiteSpace(Arguments) ? string.Empty : " " + Arguments);

    public static ShellCommand Resolve(string? shell)
    {
        return (shell ?? "pwsh").Trim().ToLowerInvariant() switch
        {
            "pwsh" or "powershell7" => new("pwsh", "pwsh.exe", "-NoLogo"),
            "powershell" or "windowspowershell" => new("powershell", "powershell.exe", "-NoLogo"),
            "cmd" => new("cmd", "cmd.exe", "/Q"),
            _ => throw new ArgumentException("只允许 shell=pwsh、powershell 或 cmd。", nameof(shell))
        };
    }

    private static string Quote(string value)
    {
        if (value.Length > 1 && value[0] == '"' && value[^1] == '"') return value;
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }
}
