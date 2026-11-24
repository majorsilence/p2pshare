using MonoTorrent;
using P2PShare.Core.Engine;
using P2PShare.Core.Net;
using P2PShare.Core.Settings;

namespace P2PShare.Core.Tests;

/// <summary>
/// Checks that trackers reach the places that use them. Nothing here announces to a real tracker:
/// the engine runs with discovery off and we only inspect what it was configured with.
/// </summary>
public class EngineTrackerTests
{
    private const string MagnetHash = "0123456789ABCDEF0123456789ABCDEF01234567";
    private static readonly string[] TwoTrackers =
    {
        "udp://one.example.org:1337/announce",
        "http://two.example.org:8080/announce",
    };

    private static TorrentEngine NewEngine(TempDir dir, string name) => new(new EngineOptions(
        dir.File($"{name}-downloads"), dir.File($"{name}-cache"), Net.FreeTcpPort(), 0, 0, 0, EnableNetworkDiscovery: false));

    private static string MakeFile(TempDir dir, string name = "sample.bin")
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray());
        return path;
    }

    private static string[] Normalised(IEnumerable<string> urls) =>
        urls.Select(u => new Uri(u).ToString()).OrderBy(u => u, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task A_magnet_download_gets_the_trackers()
    {
        using var dir = new TempDir();
        await using var engine = NewEngine(dir, "a");

        await engine.AddMagnetAsync($"magnet:?xt=urn:btih:{MagnetHash}", null, TwoTrackers);

        Assert.Equal(Normalised(TwoTrackers), Normalised(engine.GetTrackerUrls(MagnetHash)));
    }

    [Fact]
    public async Task A_magnet_keeps_its_own_trackers_and_gets_the_extras_without_repeats()
    {
        using var dir = new TempDir();
        await using var engine = NewEngine(dir, "a");
        var own = Uri.EscapeDataString("udp://one.example.org:1337/announce");
        var theirs = Uri.EscapeDataString("udp://theirs.example.org:6969/announce");

        await engine.AddMagnetAsync($"magnet:?xt=urn:btih:{MagnetHash}&tr={theirs}&tr={own}", null, TwoTrackers);

        var urls = engine.GetTrackerUrls(MagnetHash);
        Assert.Equal(3, urls.Count);
        Assert.Contains(urls, u => u.Contains("theirs.example.org", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.Contains("two.example.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_magnet_without_a_tracker_list_gets_none()
    {
        using var dir = new TempDir();
        await using var engine = NewEngine(dir, "a");

        await engine.AddMagnetAsync($"magnet:?xt=urn:btih:{MagnetHash}");
        Assert.Empty(engine.GetTrackerUrls(MagnetHash));
    }

    [Fact]
    public async Task A_torrent_file_download_gets_the_trackers_added()
    {
        using var dir = new TempDir();
        string torrentPath;
        string hash;
        await using (var maker = NewEngine(dir, "maker"))
        {
            var shared = await maker.ShareAsync(MakeFile(dir), dir.File("made"));
            hash = shared.InfoHashHex;
            torrentPath = Directory.GetFiles(dir.File("made"), "*.torrent").Single();
            Assert.Empty(maker.GetTrackerUrls(hash)); // created without any
        }

        await using var downloader = NewEngine(dir, "downloader");
        await downloader.AddTorrentFileAsync(torrentPath, null, TwoTrackers);

        Assert.Equal(Normalised(TwoTrackers), Normalised(downloader.GetTrackerUrls(hash)));
    }

    [Fact]
    public async Task A_private_torrent_is_left_with_only_its_own_trackers()
    {
        using var dir = new TempDir();
        var creator = new TorrentCreator(TorrentType.V1Only) { StoreMD5 = false, StoreSHA1 = true, Private = true };
        var dictionary = await creator.CreateAsync(new TorrentFileSource(MakeFile(dir)));
        var torrentPath = dir.File("private.torrent");
        File.WriteAllBytes(torrentPath, dictionary.Encode());
        var hash = Torrent.Load(dictionary).InfoHashes.V1OrV2.ToHex();

        await using var engine = NewEngine(dir, "a");
        await engine.AddTorrentFileAsync(torrentPath, null, TwoTrackers);

        Assert.Empty(engine.GetTrackerUrls(hash));
    }

    [Fact]
    public async Task A_new_share_carries_the_trackers_in_its_torrent_file()
    {
        using var dir = new TempDir();
        await using var engine = NewEngine(dir, "a");

        await engine.ShareAsync(MakeFile(dir), dir.File("made"), TwoTrackers);

        var torrent = await Torrent.LoadAsync(Directory.GetFiles(dir.File("made"), "*.torrent").Single());
        var announced = torrent.AnnounceUrls.SelectMany(tier => tier).ToArray();
        Assert.Equal(Normalised(TwoTrackers), Normalised(announced));
        Assert.Equal(TwoTrackers.Length, torrent.AnnounceUrls.Count); // one tier each, so all get announced to
    }

    [Fact]
    public async Task Trackers_do_not_change_the_info_hash_of_a_share()
    {
        using var dir = new TempDir();
        var file = MakeFile(dir);

        string without, with;
        await using (var a = NewEngine(dir, "a")) without = (await a.ShareAsync(file, dir.File("made-a"))).InfoHashHex;
        await using (var b = NewEngine(dir, "b")) with = (await b.ShareAsync(file, dir.File("made-b"), TwoTrackers)).InfoHashHex;

        Assert.Equal(without, with);
    }

    [Fact]
    public async Task Sharing_with_no_trackers_writes_a_torrent_without_any()
    {
        using var dir = new TempDir();
        await using var engine = NewEngine(dir, "a");

        await engine.ShareAsync(MakeFile(dir), dir.File("made"), Array.Empty<string>());

        var torrent = await Torrent.LoadAsync(Directory.GetFiles(dir.File("made"), "*.torrent").Single());
        Assert.Empty(torrent.AnnounceUrls.SelectMany(tier => tier));
    }

    // ---- through the service, i.e. what Settings actually controls -------------------------

    private static AppSettings OfflineSettings(TempDir dir)
    {
        var settings = AppSettings.Load(dir.Path);
        settings.DownloadsDirectory = dir.File("downloads");
        settings.ListenPort = Net.FreeTcpPort();
        settings.CrawlerPort = 0;
        return settings;
    }

    [Fact]
    public async Task The_service_uses_the_settings_list_and_picks_up_edits_without_a_restart()
    {
        using var dir = new TempDir();
        var settings = OfflineSettings(dir);
        await using var service = new P2PShareService(settings, enableNetwork: false);
        await service.StartAsync();

        // Default list first...
        var first = MakeFile(dir, "first.bin");
        Assert.True(await service.AddSharedPathAsync(first));
        var firstTorrent = await Torrent.LoadAsync(Path.Combine(settings.AppDataDirectory, "torrents", "first.bin.torrent"));
        Assert.Equal(Normalised(TrackerList.Defaults), Normalised(firstTorrent.AnnounceUrls.SelectMany(t => t)));

        // ...then an edit made in the Settings tab applies to the very next thing added.
        lock (settings.SyncRoot) settings.Trackers = new List<string> { "udp://edited.example.org:1337/announce" };
        var second = MakeFile(dir, "second.bin");
        Assert.True(await service.AddSharedPathAsync(second));
        var secondTorrent = await Torrent.LoadAsync(Path.Combine(settings.AppDataDirectory, "torrents", "second.bin.torrent"));
        Assert.Equal(new[] { new Uri("udp://edited.example.org:1337/announce").ToString() }, secondTorrent.AnnounceUrls.SelectMany(t => t).Select(u => new Uri(u).ToString()));

        // A magnet download added now gets the edited list too.
        await service.AddDownloadAsync($"magnet:?xt=urn:btih:{MagnetHash}");
        Assert.Equal(new[] { new Uri("udp://edited.example.org:1337/announce").ToString() },
            service.Engine.GetTrackerUrls(MagnetHash).Select(u => new Uri(u).ToString()));
    }

    [Fact]
    public async Task An_empty_list_means_downloads_use_the_dht_only()
    {
        using var dir = new TempDir();
        var settings = OfflineSettings(dir);
        settings.Trackers = new List<string>();
        await using var service = new P2PShareService(settings, enableNetwork: false);
        await service.StartAsync();

        await service.AddDownloadAsync($"magnet:?xt=urn:btih:{MagnetHash}");

        Assert.Empty(service.Engine.GetTrackerUrls(MagnetHash));
    }
}
