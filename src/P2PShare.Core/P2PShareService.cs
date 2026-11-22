using System.Threading.Channels;
using P2PShare.Core.Dht;
using P2PShare.Core.Engine;
using P2PShare.Core.Search;
using P2PShare.Core.Settings;

namespace P2PShare.Core;

/// <summary>
/// Wires the pieces together: the DHT crawler observes info-hashes circulating on the public
/// network, a small pool of workers tries to resolve each one's real metadata through the
/// torrent engine, and anything resolved lands in the local search index. This is the only class
/// the UI talks to.
/// </summary>
public sealed class P2PShareService : IAsyncDisposable
{
    private const int ResolverWorkerCount = 4;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(25);

    private readonly Channel<string> _resolutionQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _attemptedLock = new();
    private readonly List<Task> _workers = new();
    private CancellationTokenSource? _cts;

    public AppSettings Settings { get; }
    public DhtCrawler Crawler { get; }
    public TorrentEngine Engine { get; }
    public SearchIndex Index { get; }

    public event Action<string>? ActivityLog;
    public event Action<SearchResult>? TorrentIndexed;

    public P2PShareService(AppSettings settings)
    {
        Settings = settings;
        Directory.CreateDirectory(settings.AppDataDirectory);

        Engine = new TorrentEngine(new EngineOptions(
            settings.DownloadsDirectory,
            Path.Combine(settings.AppDataDirectory, "cache"),
            settings.ListenPort,
            settings.DhtPort,
            settings.MaxDownloadKBps * 1024,
            settings.MaxUploadKBps * 1024));

        Index = new SearchIndex(Path.Combine(settings.AppDataDirectory, "search.db"));

        Crawler = new DhtCrawler();
        Crawler.InfoHashObserved += OnInfoHashObserved;
        Crawler.Log += msg => ActivityLog?.Invoke(msg);
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        for (int i = 0; i < ResolverWorkerCount; i++)
            _workers.Add(Task.Run(() => ResolveWorkerAsync(_cts.Token)));

        await Crawler.StartAsync(Settings.CrawlerPort, _cts.Token).ConfigureAwait(false);
        ActivityLog?.Invoke($"DHT crawler listening on UDP {Settings.CrawlerPort}");

        foreach (var path in Settings.SharedPaths.ToList())
        {
            try
            {
                await Engine.ShareAsync(path, Path.Combine(Settings.AppDataDirectory, "torrents")).ConfigureAwait(false);
                ActivityLog?.Invoke($"Resumed seeding: {path}");
            }
            catch (Exception ex)
            {
                ActivityLog?.Invoke($"Could not resume seeding {path}: {ex.Message}");
            }
        }
    }

    public async Task<bool> AddSharedPathAsync(string path)
    {
        try
        {
            await Engine.ShareAsync(path, Path.Combine(Settings.AppDataDirectory, "torrents")).ConfigureAwait(false);
            if (!Settings.SharedPaths.Contains(path))
            {
                Settings.SharedPaths.Add(path);
                Settings.Save();
            }
            return true;
        }
        catch (Exception ex)
        {
            ActivityLog?.Invoke($"Could not share {path}: {ex.Message}");
            return false;
        }
    }

    private void OnInfoHashObserved(InfoHashObservation observation)
    {
        var hex = observation.InfoHash.ToHex();
        lock (_attemptedLock)
        {
            if (!_attempted.Add(hex)) return;
        }
        _resolutionQueue.Writer.TryWrite(hex);
    }

    private async Task ResolveWorkerAsync(CancellationToken token)
    {
        try
        {
            await foreach (var hex in _resolutionQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var metadata = await Engine.TryFetchMetadataAsync(hex, MetadataTimeout, token).ConfigureAwait(false);
                if (metadata is null) continue;

                Index.UpsertResolved(metadata);
                var result = Index.Search(metadata.Name, 1).FirstOrDefault(r => r.InfoHashHex.Equals(hex, StringComparison.OrdinalIgnoreCase));
                ActivityLog?.Invoke($"Indexed \"{metadata.Name}\" ({FormatSize(metadata.TotalSize)}, {metadata.Files.Count} file(s))");
                if (result is not null) TorrentIndexed?.Invoke(result);
            }
        }
        catch (OperationCanceledException) { }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _resolutionQueue.Writer.TryComplete();
        await Crawler.StopAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        await Engine.DisposeAsync().ConfigureAwait(false);
        Index.Dispose();
    }
}
