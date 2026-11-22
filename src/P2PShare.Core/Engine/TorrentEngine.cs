using System.Net;
using MonoTorrent;
using MonoTorrent.Client;

namespace P2PShare.Core.Engine;

public sealed record EngineOptions(
    string DownloadsDirectory,
    string CacheDirectory,
    int ListenPort,
    int DhtPort,
    int MaximumDownloadRateBytesPerSecond,
    int MaximumUploadRateBytesPerSecond);

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
            DhtEndPoint = new IPEndPoint(IPAddress.Any, options.DhtPort),
            AllowPortForwarding = true,
            AllowLocalPeerDiscovery = true,
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
    public async Task<FetchedMetadata?> TryFetchMetadataAsync(string infoHashHex, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            var magnet = new MagnetLink(InfoHash.FromHex(infoHashHex));
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

    public async Task<TransferSnapshot> StartDownloadAsync(string magnetUri, string? saveDirectory = null)
    {
        var magnet = MagnetLink.Parse(magnetUri);
        var manager = await _engine.AddAsync(magnet, saveDirectory ?? DownloadsDirectory).ConfigureAwait(false);
        await manager.StartAsync().ConfigureAwait(false);
        return ToSnapshot(manager);
    }

    /// <summary>Creates a .torrent from an existing local file or folder and starts seeding it.</summary>
    public async Task<TransferSnapshot> ShareAsync(string sourcePath, string torrentOutputDirectory)
    {
        var fileSource = new TorrentFileSource(sourcePath);
        var creator = new TorrentCreator(TorrentType.V1Only) { StoreMD5 = false, StoreSHA1 = true };
        var dictionary = await creator.CreateAsync(fileSource).ConfigureAwait(false);
        var torrent = Torrent.Load(dictionary);

        Directory.CreateDirectory(torrentOutputDirectory);
        var torrentPath = Path.Combine(torrentOutputDirectory, $"{torrent.Name}.torrent");
        await File.WriteAllBytesAsync(torrentPath, dictionary.Encode()).ConfigureAwait(false);

        var containingDirectory = Directory.Exists(sourcePath) ? Path.GetDirectoryName(sourcePath.TrimEnd(Path.DirectorySeparatorChar))! : Path.GetDirectoryName(sourcePath)!;
        var manager = await _engine.AddAsync(torrent, containingDirectory).ConfigureAwait(false);
        lock (_seedOnlyInfoHashes) _seedOnlyInfoHashes.Add(manager.InfoHashes.V1OrV2.ToHex());
        await manager.StartAsync().ConfigureAwait(false);
        return ToSnapshot(manager);
    }

    public IReadOnlyList<TransferSnapshot> GetSnapshots() => _engine.Torrents.Select(ToSnapshot).ToList();

    public async Task PauseAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.PauseAsync());

    public async Task ResumeAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.StartAsync());

    public async Task StopAsync(string infoHashHex) => await WithManager(infoHashHex, m => m.StopAsync());

    public async Task RemoveAsync(string infoHashHex)
    {
        var manager = Find(infoHashHex);
        if (manager is not null) await _engine.RemoveAsync(manager).ConfigureAwait(false);
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
        await _engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _engine.Dispose();
    }
}
