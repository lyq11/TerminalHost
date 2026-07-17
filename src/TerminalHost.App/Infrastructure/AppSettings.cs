using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalHost.Infrastructure;

public sealed record AppSettings(int ApiPort, string ApiToken)
{
    public static AppSettings LoadOrCreate()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalHost");
        var path = Path.Combine(folder, "settings.json");
        Directory.CreateDirectory(folder);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is { ApiPort: > 0, ApiToken.Length: > 20 }) return loaded;
            }
        }
        catch
        {
            // A damaged settings file is replaced below.
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var settings = new AppSettings(8765, token);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settings;
    }
}
