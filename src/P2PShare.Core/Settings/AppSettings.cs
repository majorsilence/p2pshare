using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2PShare.Core.Settings;

public sealed class AppSettings
{
    public string AppDataDirectory { get; set; } = DefaultAppDataDirectory();
    public string DownloadsDirectory { get; set; } = DefaultDownloadsDirectory();
    public int ListenPort { get; set; } = 51413;
    public int DhtPort { get; set; } = 51414;
    public int CrawlerPort { get; set; } = 6969;
    public int MaxDownloadKBps { get; set; } = 0;
    public int MaxUploadKBps { get; set; } = 0;
    public string Theme { get; set; } = "Y2K Green";
    public List<string> SharedPaths { get; set; } = new();

    [JsonIgnore]
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static string DefaultAppDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".p2pshare");

    public static string DefaultDownloadsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "P2PShare Downloads");

    public static AppSettings Load()
    {
        var path = Path.Combine(DefaultAppDataDirectory(), "settings.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null) return loaded;
            }
            catch
            {
                // Fall through to defaults if the settings file is corrupt.
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
