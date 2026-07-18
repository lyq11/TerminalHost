using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalHost.Infrastructure;

public sealed record AppSettings(int ApiPort, string ApiToken, bool McpEnabled = false)
{
    private static readonly string SettingsFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalHost");
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(SettingsFolder);

        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is { ApiPort: > 0, ApiToken.Length: > 20 }) return loaded;
            }
        }
        catch
        {
            // A damaged settings file is replaced below.
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var settings = new AppSettings(8765, token, false);
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
