using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalHost.Infrastructure;

public sealed record SessionProfile(string Name, string Shell, string WorkingDirectory);

public sealed record AppSettings
{
    public static readonly string[] AllMcpTools =
    {
        "terminal_list_sessions", "terminal_get_session", "terminal_snapshot",
        "terminal_create_session", "terminal_write", "terminal_execute",
        "terminal_signal", "terminal_resize", "terminal_stop_session", "terminal_ping"
    };

    private static readonly string SettingsPath = ResolveSettingsPath();
    private static readonly string SettingsFolder = Path.GetDirectoryName(SettingsPath)!;

    public int ApiPort { get; init; } = 8765;
    public string ApiToken { get; init; } = string.Empty;
    public bool McpEnabled { get; init; }
    public int McpPort { get; init; } = 8766;
    public string McpTransport { get; init; } = "http";
    public string McpAuthToken { get; init; } = string.Empty;
    public string DefaultShell { get; init; } = "pwsh";
    public string DefaultWorkingDirectory { get; init; } = string.Empty;
    public string TerminalFontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public int TerminalFontSize { get; init; } = 14;
    public string TerminalTheme { get; init; } = "dark";
    public double WindowWidth { get; init; } = 1180;
    public double WindowHeight { get; init; } = 760;
    public bool MinimizeToTray { get; init; }
    public bool RestoreSessions { get; init; } = true;
    public string[] RecentDirectories { get; init; } = Array.Empty<string>();
    public SessionProfile[] SavedSessions { get; init; } = Array.Empty<SessionProfile>();
    public string[] McpAllowedTools { get; init; } = AllMcpTools.ToArray();
    public string[] McpAllowedDirectories { get; init; } = Array.Empty<string>();
    public bool McpConfirmDangerousCommands { get; init; } = true;

    public static string FolderPath => SettingsFolder;
    public static string FilePath => SettingsPath;

    public static AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(SettingsFolder);
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(SettingsPath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
        }
        catch (Exception ex)
        {
            AppLog.Error("读取设置失败，将使用安全默认值。", ex);
        }

        var settings = Normalize(loaded ?? new AppSettings());
        settings.Save();
        return settings;
    }

    private static string ResolveSettingsPath()
    {
        string? overridden = Environment.GetEnvironmentVariable("TERMINALHOST_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalHost", "settings.json");
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        string temporaryPath = SettingsPath + ".tmp";
        string json = JsonSerializer.Serialize(Normalize(this), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        string token = settings.ApiToken;
        if (string.IsNullOrWhiteSpace(token) || token.Length < 20)
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        string defaultDirectory = settings.DefaultWorkingDirectory;
        if (string.IsNullOrWhiteSpace(defaultDirectory) || !Directory.Exists(defaultDirectory))
            defaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] tools = settings.McpAllowedTools?.Where(AllMcpTools.Contains).Distinct().ToArray()
            ?? AllMcpTools.ToArray();

        return settings with
        {
            ApiPort = Math.Clamp(settings.ApiPort, 1024, 65535),
            ApiToken = token,
            McpPort = Math.Clamp(settings.McpPort, 1024, 65535),
            McpTransport = string.Equals(settings.McpTransport, "stdio", StringComparison.OrdinalIgnoreCase) ? "stdio" : "http",
            McpEnabled = !string.Equals(settings.McpTransport, "stdio", StringComparison.OrdinalIgnoreCase) && settings.McpEnabled,
            DefaultShell = settings.DefaultShell is "pwsh" or "powershell" or "cmd" ? settings.DefaultShell : "pwsh",
            DefaultWorkingDirectory = defaultDirectory,
            TerminalFontSize = Math.Clamp(settings.TerminalFontSize, 9, 32),
            TerminalTheme = string.Equals(settings.TerminalTheme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
            WindowWidth = Math.Clamp(settings.WindowWidth, 760, 3840),
            WindowHeight = Math.Clamp(settings.WindowHeight, 480, 2160),
            RecentDirectories = settings.RecentDirectories?.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() ?? Array.Empty<string>(),
            SavedSessions = settings.SavedSessions?
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new SessionProfile(
                    x.Name.Trim(),
                    x.Shell is "pwsh" or "powershell" or "cmd" ? x.Shell : settings.DefaultShell,
                    Directory.Exists(x.WorkingDirectory) ? x.WorkingDirectory : defaultDirectory))
                .Take(20).ToArray() ?? Array.Empty<SessionProfile>(),
            McpAllowedTools = tools,
            McpAllowedDirectories = settings.McpAllowedDirectories?.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
        };
    }
}
