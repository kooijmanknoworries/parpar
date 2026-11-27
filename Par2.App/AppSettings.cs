using System.IO;
using System.Text.Json;

namespace Par2.App;

/// <summary>
/// User options for the ParPar GUI, persisted as JSON in %APPDATA%\ParPar\settings.json.
/// </summary>
public sealed class AppSettings
{
    // Create tab
    public string ProtectDirectory { get; set; } = "";
    public bool ProtectAllFiles { get; set; } = true;
    public int RedundancyPercent { get; set; } = 10;
    public string BlockSizeMode { get; set; } = "auto"; // "auto" or "manual"
    public int ManualBlockSizeKb { get; set; } = 64;
    public int RecoveryFileCount { get; set; } = 0; // 0 = default
    public string RecoveryFileScheme { get; set; } = "auto"; // "auto" | "uniform" | "limited"
    public int FirstRecoveryBlock { get; set; } = 0;

    // Monitor tab
    public string MonitorDirectory { get; set; } = "";
    public int MonitorIntervalSeconds { get; set; } = 5;
    public int MonitorThresholdPercent { get; set; } = 100;

    public static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ParPar");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded != null)
                    return loaded;
            }
        }
        catch
        {
            // fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // settings are best-effort
        }
    }
}
