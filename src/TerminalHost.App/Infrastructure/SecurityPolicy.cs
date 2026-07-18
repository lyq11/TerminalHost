using System.Text.RegularExpressions;

namespace TerminalHost.Infrastructure;

public sealed class SecurityPolicy
{
    private readonly AppSettings _settings;
    private readonly Regex[] _dangerousCommandPatterns;

    public SecurityPolicy(AppSettings settings)
    {
        _settings = settings;
        _dangerousCommandPatterns = settings.DangerousCommandRules
            .Select(rule => new Regex(Regex.Escape(rule).Replace("\\*", ".*"),
                RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
    }

    public void AuthorizeMcp(string tool, string? workingDirectory, string? command, bool dangerousConfirmed)
    {
        if (!_settings.McpAllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"MCP 工具已被安全策略禁用：{tool}");

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            ValidateWorkingDirectory(workingDirectory);

        if (_settings.McpConfirmDangerousCommands && !string.IsNullOrWhiteSpace(command) &&
            _dangerousCommandPatterns.Any(pattern => pattern.IsMatch(command)) && !dangerousConfirmed)
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
