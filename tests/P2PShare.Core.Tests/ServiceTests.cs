using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent.BEncoding;
using P2PShare.Core.Engine;
using P2PShare.Core.Search;
using P2PShare.Core.Settings;

namespace P2PShare.Core.Tests;

/// <summary>
/// End-to-end tests of <see cref="P2PShareService"/> with networking switched off: the engine does
/// no DHT/UPnP/LAN discovery and the crawler has no bootstrap hosts, so everything stays on this
/// machine. They cover the persistence and pipeline behaviour that the UI relies on.
/// </summary>
public class ServiceTests
{
    private const string MagnetHash = "0123456789ABCDEF0123456789ABCDEF01234567";
    private static readonly string Magnet = $"magnet:?xt=urn:btih:{MagnetHash}&dn=Example";

    private static AppSettings OfflineSettings(TempDir dir)
    {
        var settings = AppSettings.Load(dir.Path);
        settings.DownloadsDirectory = dir.File("downloads");
        settings.ListenPort = Net.FreeTcpPort();
        settings.CrawlerPort = 0;
        return settings;
    }

    private static P2PShareService NewService(AppSettings settings) => new(settings, enableNetwork: false);

    [Fact]
    public async Task A_magnet_download_is_remembered_and_comes_back_after_a_restart()
    {
        using var dir = new TempDir();
        var settings = OfflineSettings(dir);

        await using (var service = NewService(settings))
        {
            await service.StartAsync();
            var snapshot = await service.AddDownloadAsync(Magnet);

            Assert.Equal(MagnetHash, snapshot.InfoHashHex, ignoreCase: true);
            var saved = Assert.Single(AppSettings.Load(dir.Path).Downloads);
            Assert.Equal(MagnetHash, saved.InfoHashHex, ignoreCase: true);
        }

        await using (var restarted = NewService(AppSettings.Load(dir.Path)))
        {
            await restarted.StartAsync();

            var resumed = Assert.Single(restarted.Engine.GetSnapshots());
            Assert.Equal(MagnetHash, resumed.InfoHashHex, ignoreCase: true);
        }
    }

    [Fact]
    public async Task Offline_mode_never_asks_the_router_for_a_port_mapping()
    {
        using var dir = new TempDir();
        await using var service = NewService(OfflineSettings(dir));

        await service.StartAsync();

        Assert.Null(service.PortMapper);
    }

    [Fact]
    public async Task Adding_the_same_download_twice_keeps_one_transfer_and_one_saved_entry()
    {
        using var dir = new TempDir();
        await using var service = NewService(OfflineSettings(dir));
        await service.StartAsync();

        await service.AddDownloadAsync(Magnet);
        await service.AddDownloadAsync(MagnetHash.ToLowerInvariant()); // same torrent, typed as a bare hash

        Assert.Single(service.Engine.GetSnapshots());
        Assert.Single(service.Settings.Downloads);
    }

    [Fact]
    public async Task Removing_a_download_forgets_it_across_restarts()
    {
        using var dir = new TempDir();
        await using (var service = NewService(OfflineSettings(dir)))
        {
            await service.StartAsync();
            var snapshot = await service.AddDownloadAsync(Magnet);

            await service.RemoveTransferAsync(snapshot.InfoHashHex);

            Assert.Empty(service.Engine.GetSnapshots());
            Assert.Empty(AppSettings.Load(dir.Path).Downloads);
        }

        await using var restarted = NewService(AppSettings.Load(dir.Path));
        await restarted.StartAsync();
        Assert.Empty(restarted.Engine.GetSnapshots());
    }

    [Fact]
    public async Task Invalid_download_input_is_rejected_without_side_effects()
    {
        using var dir = new TempDir();
        await using var service = NewService(OfflineSettings(dir));
        await service.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddDownloadAsync("this is not a magnet"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddDownloadAsync(""));

        Assert.Empty(service.Engine.GetSnapshots());
        Assert.Empty(service.Settings.Downloads);
    }

    [Fact]
    public async Task A_corrupt_torrent_file_is_reported_and_not_remembered()
    {
        using var dir = new TempDir();
        var bogus = dir.File("bogus.torrent");
        File.WriteAllText(bogus, "definitely not bencoded torrent data");
        await using var service = NewService(OfflineSettings(dir));
        await service.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => service.AddDownloadAsync(bogus));

        Assert.Empty(service.Settings.Downloads);
    }

    [Fact]
    public async Task A_shared_file_is_seeded_remembered_and_can_be_unshared()
    {
        using var dir = new TempDir();
        var file = dir.File("share-me.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray());
        var settings = OfflineSettings(dir);

        await using (var service = NewService(settings))
        {
            await service.StartAsync();

            Assert.True(await service.AddSharedPathAsync(file));
            Assert.True(await service.AddSharedPathAsync(file)); // sharing twice must not throw or duplicate

            var seed = Assert.Single(service.Engine.GetSnapshots());
            Assert.True(seed.IsSeedOnly);
            Assert.Equal("share-me.bin", seed.Name);
            Assert.Equal(new[] { file }, AppSettings.Load(dir.Path).SharedPaths);

            // Once hashing finishes the torrent is complete, so it should be seeding.
            Assert.True(await Net.WaitUntilAsync(
                () => service.Engine.GetSnapshots().Single().State == MonoTorrent.Client.TorrentState.Seeding,
                TimeSpan.FromSeconds(30)));

            await service.RemoveTransferAsync(seed.InfoHashHex);

            Assert.Empty(service.Engine.GetSnapshots());
            Assert.Empty(AppSettings.Load(dir.Path).SharedPaths);
        }

        Assert.True(File.Exists(file), "unsharing must never delete the user's file");
    }

    [Fact]
    public async Task Sharing_a_path_that_does_not_exist_fails_without_being_remembered()
    {
        using var dir = new TempDir();
        await using var service = NewService(OfflineSettings(dir));
        await service.StartAsync();
        var log = new ConcurrentQueue<string>();
        service.SubscribeToActivity(e => log.Enqueue(e.Message));

        var ok = await service.AddSharedPathAsync(dir.File("nope.bin"));

        Assert.False(ok);
        Assert.Empty(service.Settings.SharedPaths);
        Assert.Contains(log, m => m.Contains("Could not share"));
    }

    [Fact]
    public async Task A_taken_crawler_port_is_reported_but_does_not_stop_startup()
    {
        using var dir = new TempDir();
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var settings = OfflineSettings(dir);
        settings.CrawlerPort = ((IPEndPoint)squatter.Client.LocalEndPoint!).Port;
        var file = dir.File("still-shared.bin");
        File.WriteAllBytes(file, new byte[10_000]);
        settings.SharedPaths.Add(file);

        await using var service = NewService(settings);

        await service.StartAsync(); // must not throw

        // The UI builds its log view after startup has begun, so it subscribes late; the message
        // must still reach it.
        var log = new ConcurrentQueue<string>();
        service.SubscribeToActivity(e => log.Enqueue(e.Message));
        Assert.Contains(log, m => m.Contains("crawler could not start"));
        Assert.False(service.Crawler.IsRunning);
        // ...and the rest of startup still ran: the saved share was resumed.
        Assert.Single(service.Engine.GetSnapshots());
    }

    [Fact]
    public async Task Observed_hashes_are_looked_up_once_and_known_ones_only_gain_popularity()
    {
        using var dir = new TempDir();
        var settings = OfflineSettings(dir);
        var knownHash = "00000000000000000000000000000000000000aa";
        var unknownHash = Enumerable.Repeat((byte)0x22, 20).ToArray();

        // A torrent indexed in an earlier session.
        using (var index = new SearchIndex(Path.Combine(dir.Path, "search.db")))
            index.UpsertResolved(new FetchedMetadata(knownHash, "previously indexed", 1, new[] { ("f", 1L) }));

        await using (var service = NewService(settings))
        {
            await service.StartAsync();
            using var peer = Net.NewLoopbackClient();

            // Three nodes ask about the known hash, and the same unknown hash is asked about twice.
            foreach (var hash in new[] { Convert.FromHexString(knownHash), Convert.FromHexString(knownHash), Convert.FromHexString(knownHash), unknownHash, unknownHash })
            {
                await Net.RoundTripAsync(peer, service.Crawler.LocalPort, Net.KrpcQuery("get_peers", new BEncodedDictionary
                {
                    { "id", new BEncodedString(Net.Bytes(20, 9)) },
                    { "info_hash", new BEncodedString(hash) },
                }));
            }

            // The unknown hash was queued for a metadata lookup exactly once; the known one never was.
            Assert.True(await Net.WaitUntilAsync(() => service.LookupsAttempted >= 1, TimeSpan.FromSeconds(10)));
            await Task.Delay(300);
            Assert.Equal(1, service.LookupsAttempted);
        } // disposing flushes the pending popularity counts

        using var reopened = new SearchIndex(Path.Combine(dir.Path, "search.db"));
        Assert.Equal(4, reopened.Get(knownHash)!.SeenCount); // 1 original + 3 sightings
        Assert.Equal(1, reopened.TotalIndexed());            // the unknown hash was not resolvable offline
    }

    [Fact]
    public async Task A_throwing_activity_subscriber_cannot_break_the_pipeline()
    {
        using var dir = new TempDir();
        var file = dir.File("x.bin");
        File.WriteAllBytes(file, new byte[5_000]);
        await using var service = NewService(OfflineSettings(dir));
        service.SubscribeToActivity(_ => throw new InvalidOperationException("window not created yet"));
        await service.StartAsync();

        var ok = await service.AddSharedPathAsync(file);

        Assert.True(ok);
    }

    [Fact]
    public async Task Activity_history_is_bounded()
    {
        using var dir = new TempDir();
        await using var service = NewService(OfflineSettings(dir));
        await service.StartAsync();

        for (int i = 0; i < 400; i++)
            await service.AddSharedPathAsync(dir.File($"missing-{i}.bin")); // each failure logs one line

        var count = 0;
        service.SubscribeToActivity(_ => count++);
        Assert.InRange(count, 1, 300);
    }

    [Fact]
    public void FormatSize_uses_binary_units()
    {
        Assert.Equal("0 B", P2PShareService.FormatSize(0));
        Assert.Equal("1023 B", P2PShareService.FormatSize(1023));
        Assert.Equal("1 KB", P2PShareService.FormatSize(1024));
        Assert.Equal("1.5 MB", P2PShareService.FormatSize(1536 * 1024));
        Assert.Equal("2 GB", P2PShareService.FormatSize(2L * 1024 * 1024 * 1024));
        Assert.Equal("1024 TB", P2PShareService.FormatSize(1024L * 1024 * 1024 * 1024 * 1024));
    }
}
