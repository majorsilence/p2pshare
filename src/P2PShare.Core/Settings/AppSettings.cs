using System.Text.Json;
using System.Text.Json.Serialization;
using P2PShare.Core.Net;

namespace P2PShare.Core.Settings;

/// <summary>A download the user added, remembered so it can be resumed after a restart.</summary>
public sealed class SavedDownload
{
    public string InfoHashHex { get; set; } = "";

    /// <summary>A magnet URI, or the path of a .torrent file the app keeps its own copy of.</summary>
    public string Source { get; set; } = "";
}

public sealed class AppSettings
{
    /// <summary>Set this environment variable to keep all app data somewhere other than <c>~/.p2pshare</c>.</summary>
    public const string HomeEnvironmentVariable = "P2PSHARE_HOME";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string AppDataDirectory { get; set; } = DefaultAppDataDirectory();
    public string DownloadsDirectory { get; set; } = DefaultDownloadsDirectory();
    public int ListenPort { get; set; } = 51413;
    public int DhtPort { get; set; } = 51414;
    public int CrawlerPort { get; set; } = 6969;
    public int MaxDownloadKBps { get; set; } = 0;
    public int MaxUploadKBps { get; set; } = 0;
    public string Theme { get; set; } = "Y2K Green";
    public List<string> SharedPaths { get; set; } = new();
    public List<SavedDownload> Downloads { get; set; } = new();

    /// <summary>
    /// Announce URLs added to downloads and to torrents this app creates. Starts as the built-in
    /// list; the user can edit it, and an empty list means "DHT only".
    /// </summary>
    public List<string> Trackers { get; set; } = TrackerList.Defaults.ToList();

    /// <summary>Also let the background search lookups ask a few of the trackers for peers.</summary>
    public bool UseTrackersForLookups { get; set; } = true;

    /// <summary>Periodically ask the trackers how many seeders each indexed torrent has.</summary>
    public bool ScrapeSeederCounts { get; set; } = true;

    /// <summary>
    /// Held while the lists above are mutated or serialized, so a background thread adding a
    /// download can't corrupt a save that the UI thread triggers at the same moment.
    /// </summary>
    [JsonIgnore]
    public object SyncRoot { get; } = new();

    [JsonIgnore]
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static string DefaultAppDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".p2pshare");
    }

    public static string DefaultDownloadsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "P2PShare Downloads");

    public static AppSettings Load() => Load(DefaultAppDataDirectory());

    public static AppSettings Load(string appDataDirectory)
    {
        var path = Path.Combine(appDataDirectory, "settings.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    // The file may have been copied from elsewhere; wherever we found it is home.
                    loaded.AppDataDirectory = appDataDirectory;
                    loaded.SharedPaths ??= new();
                    loaded.Downloads ??= new();
                    // A settings.json from an older version has no list at all (the built-in one
                    // applies), whereas an empty list is a deliberate "no trackers". Hand edits are
                    // cleaned up the same way the Settings tab cleans them.
                    loaded.Trackers = loaded.Trackers is null
                        ? TrackerList.Defaults.ToList()
                        : TrackerList.Parse(string.Join("\n", loaded.Trackers)).Valid.ToList();
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Don't silently throw away a hand-edited or half-written file: keep a copy so the
                // user can recover it, then fall through to defaults.
                TryBackUpCorruptFile(path);
            }
        }
        return new AppSettings { AppDataDirectory = appDataDirectory };
    }

    public void Save()
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(AppDataDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Write to a temp file and swap it in, so a crash mid-write can't leave a truncated
            // settings.json (which Load would then discard).
            var tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
    }

    private static void TryBackUpCorruptFile(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); }
        catch { /* best effort */ }
    }
}
