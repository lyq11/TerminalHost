using System.Text.RegularExpressions;

namespace TerminalHost.Infrastructure;

public sealed class SecurityPolicy
{
    private static readonly Regex DangerousCommandPattern = new(
        @"(^|[;&|]\s*)(rm\s+-[^\r\n]*r[^\r\n]*f|remove-item\b[^\r\n]*-recurse|del\s+[^\r\n]*/s|rmdir\s+[^\r\n]*/s|rd\s+[^\r\n]*/s|format\b|diskpart\b|shutdown\b|stop-computer\b|restart-computer\b|git\s+reset\s+--hard|drop\s+(database|table)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppSettings _settings;

    public SecurityPolicy(AppSettings settings) => _settings = settings;

    public void AuthorizeMcp(string tool, string? workingDirectory, string? command, bool dangerousConfirmed)
    {
        if (!_settings.McpAllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"MCP 工具已被安全策略禁用：{tool}");

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            ValidateWorkingDirectory(workingDirectory);

        if (_settings.McpConfirmDangerousCommands && !string.IsNullOrWhiteSpace(command) &&
            DangerousCommandPattern.IsMatch(command) && !dangerousConfirmed)
        {
            throw new UnauthorizedAccessException("检测到危险命令；请在工具调用中明确设置 confirmDangerous=true。 ");
        }

        AppLog.Audit($"MCP 授权 tool={tool} cwd={workingDirectory ?? "-"} dangerousConfirmed={dangerousConfirmed}");
    }

    public void ValidateWorkingDirectory(string workingDirectory)
    {
        if (_settings.McpAllowedDirectories.Length == 0) return;
        string candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(workingDirectory.Trim().Trim('"')));
        bool allowed = _settings.McpAllowedDirectories.Any(root => IsWithin(candidate, Path.GetFullPath(root)));
        if (!allowed)
            throw new UnauthorizedAccessException($"工作目录不在 MCP 允许范围内：{candidate}");
    }

    private static bool IsWithin(string candidate, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
