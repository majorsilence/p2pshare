using System.Net;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using P2PShare.Core.Net;

namespace P2PShare.Core.Engine;

public sealed record EngineOptions(
    string DownloadsDirectory,
    string CacheDirectory,
    int ListenPort,
    int DhtPort,
    int MaximumDownloadRateBytesPerSecond,
    int MaximumUploadRateBytesPerSecond,
    // Turn off DHT, UPnP/NAT-PMP and LAN peer discovery. Only meant for tests, which must not
    // reach out to the real network; leave true for the real app.
    bool EnableNetworkDiscovery = true);

public sealed record FetchedMetadata(string InfoHashHex, string Name, long TotalSize, IReadOnlyList<(string Path, long Length)> Files);

public sealed record TransferSnapshot(
    string InfoHashHex,
    string Name,
    long TotalSize,
    double ProgressPercent,
    long DownloadRateBps,
    long UploadRateBps,
    int AvailablePeers,
    TorrentState State,
    string SavePath,
    bool IsSeedOnly);

/// <summary>
/// Thin wrapper around MonoTorrent's <see cref="ClientEngine"/>: real BitTorrent download/seed/DHT
/// peer-discovery, plus a narrow metadata-only fetch path used by the search pipeline (given an
/// info-hash the DHT crawler observed, resolve its name/files without committing to a download).
/// </summary>
public sealed class TorrentEngine : IAsyncDisposable
{
    private readonly ClientEngine _engine;
    private readonly HashSet<string> _seedOnlyInfoHashes = new();

    public TorrentEngine(EngineOptions options)
    {
        Directory.CreateDirectory(options.DownloadsDirectory);
        Directory.CreateDirectory(options.CacheDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = options.CacheDirectory,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, options.ListenPort),
            },
            DhtEndPoint = options.EnableNetworkDiscovery ? new IPEndPoint(IPAddress.Any, options.DhtPort) : null,
            AllowPortForwarding = options.EnableNetworkDiscovery,
            AllowLocalPeerDiscovery = options.EnableNetworkDiscovery,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            MaximumDownloadRate = options.MaximumDownloadRateBytesPerSecond,
            MaximumUploadRate = options.MaximumUploadRateBytesPerSecond,
        }.ToSettings();

        _engine = new ClientEngine(settings);
        DownloadsDirectory = options.DownloadsDirectory;
    }

    public string DownloadsDirectory { get; }

    public IDht Dht => _engine.Dht;

    public long TotalDownloadRateBps => _engine.TotalDownloadRate;

    public long TotalUploadRateBps => _engine.TotalUploadRate;

    /// <summary>
    /// Resolves name/size/file-list for an info-hash via the BitTorrent extension protocol
    /// (BEP 9), using whatever peers MonoTorrent's own DHT and peer exchange can find for it.
    /// Many info-hashes seen circulating on the DHT will have few or no reachable peers by the
    /// time we look - that failure is expected and simply means nothing useful to index.
    /// </summary>
    public async Task<FetchedMetadata?> TryFetchMetadataAsync(
        string infoHashHex, TimeSpan timeout, CancellationToken token, IReadOnlyList<string>? trackers = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            // Trackers, where given, are one more place to find peers for a hash the DHT alone
            // often can't.
            var magnet = new MagnetLink(
                InfoHash.FromHex(infoHashHex), null, trackers is { Count: > 0 } ? trackers.ToList() : null, null, null);
            var metadata = await _engine.DownloadMetadataAsync(magnet, cts.Token).ConfigureAwait(false);
            var torrent = Torrent.Load(metadata.Span);
            var files = torrent.Files.Select(f => (f.Path, f.Length)).ToList();
            return new FetchedMetadata(infoHashHex, torrent.Name, torrent.Size, files);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts downloading a magnet link. Adding something already in the engine is not an error;
    /// the existing transfer is returned.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid magnet URI.</exception>
    public async Task<TransferSnapshot> AddMagnetAsync(
        string magnetUri, string? saveDirectory = null, IReadOnlyList<string>? trackers = null)
    {
        var magnet = WithTrackers(MagnetLink.Parse(magnetUri), trackers);
        var existing = Find(magnet.InfoHashes.V1OrV2.ToHex());
        if (existing is not null) return ToSnapshot(existing);

        var manager = await _engine.AddAsync(magnet, saveDirectory ?? DownloadsDirectory).ConfigureAwait(false);
        await manager.StartAsync().ConfigureAwait(false);
        return ToSnapshot(manager);
    }

    /// <summary>Starts downloading from a .torrent file on disk. Already-added torrents return the existing transfer.</summary>
    public async Task<TransferSnapshot> AddTorrentFileAsync(
        string torrentPath, string? saveDirectory = null, IReadOnlyList<string>? trackers = null)
    {
        var torrent = await Torrent.LoadAsync(torrentPath).ConfigureAwait(false);
        var existing = Find(torrent.InfoHashes.V1OrV2.ToHex());
        if (existing is not null) return ToSnapshot(existing);

        var manager = await _engine.AddAsync(torrent, saveDirectory ?? DownloadsDirectory).ConfigureAwait(false);

        // A private torrent's own tracker is the only one its swarm wants; leave those alone.
        if (!torrent.IsPrivate) await AddExtraTrackersAsync(manager, torrent, trackers).ConfigureAwait(false);
        await manager.StartAsync().ConfigureAwait(false);
        return ToSnapshot(manager);
    }

    /// <summary>Creates a .torrent from an existing local file or folder and starts seeding it.</summary>
    public async Task<TransferSnapshot> ShareAsync(
        string sourcePath, string torrentOutputDirectory, IReadOnlyList<string>? trackers = null)
    {
        var fileSource = new TorrentFileSource(sourcePath);
        var creator = new TorrentCreator(TorrentType.V1Only) { StoreMD5 = false, StoreSHA1 = true };
        var dictionary = await creator.CreateAsync(fileSource).ConfigureAwait(false);

        // Embedded in the .torrent so that anyone who gets the file can find us through the
        // trackers as well as the DHT. The info-hash does not depend on them.
        ApplyTrackers(dictionary, trackers);
        var torrent = Torrent.Load(dictionary);

        // Sharing the same thing twice would make the engine throw a duplicate-torrent error.
        var existing = Find(torrent.InfoHashes.V1OrV2.ToHex());
        if (existing is not null) return ToSnapshot(existing);

        Directory.CreateDirectory(torrentOutputDirectory);
        var torrentPath = Path.Combine(torrentOutputDirectory, $"{torrent.Name}.torrent");
        await File.WriteAllBytesAsync(torrentPath, dictionary.Encode()).ConfigureAwait(false);

        var containingDirectory = Directory.Exists(sourcePath) ? Path.GetDirectoryName(sourcePath.TrimEnd(Path.DirectorySeparatorChar))! : Path.GetDirectoryName(sourcePath)!;
        var manager = await _engine.AddAsync(torrent, containingDirectory).ConfigureAwait(false);
        lock (_seedOnlyInfoHashes) _seedOnlyInfoHashes.Add(manager.InfoHashes.V1OrV2.ToHex());
        await manager.StartAsync().ConfigureAwait(false);
        return ToSnapshot(manager);
    }

    /// <summary>The announce URLs a transfer will use, for tests and diagnostics.</summary>
    internal IReadOnlyList<string> GetTrackerUrls(string infoHashHex) =>
        Find(infoHashHex)?.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).Select(t => t.Uri.ToString()).ToList()
        ?? new List<string>();

    private static MagnetLink WithTrackers(MagnetLink magnet, IReadOnlyList<string>? trackers)
    {
        if (trackers is null || trackers.Count == 0) return magnet;

        var merged = TrackerList.Merge(magnet.AnnounceUrls, trackers); // the link's own trackers keep priority
        if (merged.Count == magnet.AnnounceUrls.Count) return magnet;
        return new MagnetLink(magnet.InfoHashes, magnet.Name, merged, magnet.Webseeds, magnet.Size);
    }

    private static async Task AddExtraTrackersAsync(TorrentManager manager, Torrent torrent, IReadOnlyList<string>? trackers)
    {
        if (trackers is null || trackers.Count == 0) return;

        var known = new HashSet<string>(torrent.AnnounceUrls.SelectMany(tier => tier), StringComparer.OrdinalIgnoreCase);
        foreach (var url in trackers)
        {
            if (known.Contains(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            try { await manager.TrackerManager.AddTrackerAsync(uri).ConfigureAwait(false); }
            catch (Exception) { /* a tracker MonoTorrent can't use isn't worth failing the download for */ }
        }
    }

    private static void ApplyTrackers(BEncodedDictionary dictionary, IReadOnlyList<string>? trackers)
    {
        if (trackers is null || trackers.Count == 0) return;

        // One tier per tracker, so a client announces to all of them rather than only the first that answers.
        var tiers = new BEncodedList();
        foreach (var url in trackers) tiers.Add(new BEncodedList { new BEncodedString(url) });
        dictionary["announce"] = new BEncodedString(trackers[0]);
        dictionary["announce-list"] = tiers;
    }

    public IReadOnlyList<TransferSnapshot> GetSnapshots() => _engine.Torrents.Select(ToSnapshot).ToList();

    public async Task PauseAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.PauseAsync());

    public async Task ResumeAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.StartAsync());

    public async Task StopAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.StopAsync());

    public async Task RemoveAsync(string infoHashHex)
    {
        var manager = Find(infoHashHex);
        if (manager is null) return;

        var hashHex = manager.InfoHashes.V1OrV2.ToHex();

        // MonoTorrent refuses to unregister a manager that is still running.
        if (manager.State != TorrentState.Stopped)
            await manager.StopAsync().ConfigureAwait(false);
        await _engine.RemoveAsync(manager).ConfigureAwait(false);
        lock (_seedOnlyInfoHashes) _seedOnlyInfoHashes.Remove(hashHex);
    }

    public void SubscribeToStateChanges(Action callback)
    {
        _engine.StatsUpdate += (_, _) => callback();
    }

    private async Task WithManager(string infoHashHex, Func<TorrentManager, Task> action)
    {
        var manager = Find(infoHashHex);
        if (manager is not null) await action(manager).ConfigureAwait(false);
    }

    private TorrentManager? Find(string infoHashHex) =>
        _engine.Torrents.FirstOrDefault(m => string.Equals(m.InfoHashes.V1OrV2.ToHex(), infoHashHex, StringComparison.OrdinalIgnoreCase));

    private TransferSnapshot ToSnapshot(TorrentManager manager)
    {
        var hashHex = manager.InfoHashes.V1OrV2.ToHex();
        bool seedOnly;
        lock (_seedOnlyInfoHashes) seedOnly = _seedOnlyInfoHashes.Contains(hashHex);
        return new TransferSnapshot(
            hashHex,
            manager.Name,
            manager.Torrent?.Size ?? 0,
            Math.Round(manager.Progress, 1),
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.Peers.Available,
            manager.State,
            manager.SavePath,
            seedOnly);
    }

    public async ValueTask DisposeAsync()
    {
        // MonoTorrent can throw from StopAllAsync (e.g. ObjectDisposedException) when a torrent is
        // still starting or hashing - such as a share resumed at launch when the user closes the
        // window straight away. We're shutting down regardless, so never let that escape.
        try { await _engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }

        try { _engine.Dispose(); }
        catch (Exception) { }
    }
}
