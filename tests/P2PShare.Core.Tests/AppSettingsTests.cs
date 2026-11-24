using P2PShare.Core.Settings;

namespace P2PShare.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults_rooted_at_the_given_directory()
    {
        using var dir = new TempDir();

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(dir.Path, settings.AppDataDirectory);
        Assert.Equal(51413, settings.ListenPort);
        Assert.Empty(settings.SharedPaths);
        Assert.Empty(settings.Downloads);
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        using var dir = new TempDir();
        var settings = AppSettings.Load(dir.Path);
        settings.ListenPort = 40000;
        settings.MaxDownloadKBps = 512;
        settings.DownloadsDirectory = dir.File("dl");
        settings.SharedPaths.Add(@"C:\music");
        settings.Downloads.Add(new SavedDownload { InfoHashHex = "abc", Source = "magnet:?xt=urn:btih:abc" });

        settings.Save();
        var reloaded = AppSettings.Load(dir.Path);

        Assert.Equal(40000, reloaded.ListenPort);
        Assert.Equal(512, reloaded.MaxDownloadKBps);
        Assert.Equal(dir.File("dl"), reloaded.DownloadsDirectory);
        Assert.Equal(new[] { @"C:\music" }, reloaded.SharedPaths);
        var download = Assert.Single(reloaded.Downloads);
        Assert.Equal("abc", download.InfoHashHex);
        Assert.Equal("magnet:?xt=urn:btih:abc", download.Source);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var dir = new TempDir();
        var settings = AppSettings.Load(dir.Path);

        settings.Save();
        settings.Save();

        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_but_is_kept_for_recovery()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ this is not json");

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(51413, settings.ListenPort);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Json_nulls_for_lists_do_not_produce_null_lists()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), """{ "SharedPaths": null, "Downloads": null, "ListenPort": 1234 }""");

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(1234, settings.ListenPort);
        Assert.NotNull(settings.SharedPaths);
        Assert.NotNull(settings.Downloads);
    }

    [Fact]
    public void Settings_written_by_an_older_version_still_load()
    {
        using var dir = new TempDir();
        // Version 1 had no Downloads list.
        File.WriteAllText(dir.File("settings.json"), """{ "ListenPort": 5555, "SharedPaths": ["D:\\stuff"] }""");

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(5555, settings.ListenPort);
        Assert.Equal(new[] { @"D:\stuff" }, settings.SharedPaths);
        Assert.Empty(settings.Downloads);
    }

    [Fact]
    public async Task Concurrent_saves_and_edits_do_not_throw()
    {
        using var dir = new TempDir();
        var settings = AppSettings.Load(dir.Path);

        var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                lock (settings.SyncRoot)
                {
                    settings.SharedPaths.Add($@"C:\p{t}-{i}");
                    settings.Save();
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(200, AppSettings.Load(dir.Path).SharedPaths.Count);
    }
}
