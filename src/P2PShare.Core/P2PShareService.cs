using System.Collections.Concurrent;
using System.Threading.Channels;
using P2PShare.Core.Dht;
using P2PShare.Core.Engine;
using P2PShare.Core.Net;
using P2PShare.Core.Search;
using P2PShare.Core.Settings;
using P2PShare.Core.Util;

namespace P2PShare.Core;

/// <summary>One line of the activity log shown on the Network tab.</summary>
public sealed record ActivityEntry(DateTime TimeLocal, string Message);

/// <summary>
/// Wires the pieces together: the DHT crawler observes info-hashes circulating on the public
/// network, a small pool of workers tries to resolve each one's real metadata through the
/// torrent engine, and anything resolved lands in the local search index. This is the only class
/// the UI talks to.
/// </summary>
public sealed class P2PShareService : IAsyncDisposable
{
    private const int ResolverWorkerCount = 4;
    private const int AttemptedCapacity = 200_000;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(25);

    // A hash that timed out may simply have had no reachable peers at that moment; try it again
    // after a while instead of writing it off for the whole session.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan SightingFlushInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _resolutionQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ExpiringSet _attempted = new(AttemptedCapacity, RetryAfter);

    // Hashes already in the search index, so re-observing one only bumps its popularity counter
    // (batched below) instead of costing another metadata fetch.
    private readonly HashSet<string> _indexed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexedLock = new();
    private readonly ConcurrentDictionary<string, int> _pendingSightings = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Task> _workers = new();
    private readonly bool _enableNetwork;
    private PortMapper? _portMapper;
    private CancellationTokenSource? _cts;
    private long _lookupsAttempted;
    private long _lookupsResolved;

    public AppSettings Settings { get; }
    public DhtCrawler Crawler { get; }
    public TorrentEngine Engine { get; }
    public SearchIndex Index { get; }

    /// <summary>Null until the crawler has started, and always null in offline (test) mode.</summary>
    internal PortMapper? PortMapper => _portMapper;

    /// <summary>Metadata lookups started, resolved, and currently waiting - shown on the Network tab.</summary>
    public long LookupsAttempted => Interlocked.Read(ref _lookupsAttempted);
    public long LookupsResolved => Interlocked.Read(ref _lookupsResolved);
    public int LookupsQueued => _resolutionQueue.Reader.Count;

    /// <summary>Raised from background threads; UI code must marshal to its own thread.</summary>
    public event Action<SearchResult>? TorrentIndexed;

    private const int MaxActivityHistory = 300;
    private readonly object _activityLock = new();
    private readonly Queue<ActivityEntry> _activityHistory = new();
    private readonly List<Action<ActivityEntry>> _activitySubscribers = new();

    /// <summary>
    /// Delivers every activity-log entry to <paramref name="handler"/>, starting with the recent
    /// history. Replaying matters because the pipeline starts logging (e.g. "crawler could not
    /// start") before the UI has built the tab that shows it. Handlers run on whatever thread
    /// logged, while an internal lock is held, so they must be quick and must marshal to the UI
    /// thread (post, don't wait) rather than do work themselves.
    /// </summary>
    public void SubscribeToActivity(Action<ActivityEntry> handler)
    {
        lock (_activityLock)
        {
            foreach (var entry in _activityHistory) SafeInvoke(handler, entry);
            _activitySubscribers.Add(handler);
        }
    }

    private void Log(string message)
    {
        var entry = new ActivityEntry(DateTime.Now, message);
        lock (_activityLock)
        {
            _activityHistory.Enqueue(entry);
            while (_activityHistory.Count > MaxActivityHistory) _activityHistory.Dequeue();
            foreach (var subscriber in _activitySubscribers) SafeInvoke(subscriber, entry);
        }
    }

    // A faulty subscriber (say, UI code reached before its window exists) must not break the pipeline.
    private static void SafeInvoke(Action<ActivityEntry> handler, ActivityEntry entry)
    {
        try { handler(entry); }
        catch { /* deliberately ignored */ }
    }

    /// <param name="enableNetwork">
    /// False keeps everything on this machine: no public DHT bootstrap, UPnP or LAN discovery.
    /// Only meant for tests; the app always passes true.
    /// </param>
    public P2PShareService(AppSettings settings, bool enableNetwork = true)
    {
        Settings = settings;
        _enableNetwork = enableNetwork;
        Directory.CreateDirectory(settings.AppDataDirectory);

        Engine = new TorrentEngine(new EngineOptions(
            settings.DownloadsDirectory,
            Path.Combine(settings.AppDataDirectory, "cache"),
            settings.ListenPort,
            settings.DhtPort,
            settings.MaxDownloadKBps * 1024,
            settings.MaxUploadKBps * 1024,
            EnableNetworkDiscovery: enableNetwork));

        Index = new SearchIndex(Path.Combine(settings.AppDataDirectory, "search.db"));
        foreach (var hash in Index.AllInfoHashes()) _indexed.Add(hash);

        Crawler = new DhtCrawler(enableNetwork ? null : Array.Empty<(string, int)>());
        Crawler.InfoHashObserved += OnInfoHashObserved;
        Crawler.Log += Log;
    }

    /// <summary>The tracker list as it is right now, so edits in Settings apply to the next download without a restart.</summary>
    private string[] TrackersSnapshot()
    {
        lock (Settings.SyncRoot) return Settings.Trackers.ToArray();
    }

    private IReadOnlyList<string> LookupTrackers()
    {
        lock (Settings.SyncRoot)
            return Settings.UseTrackersForLookups ? TrackerList.PickForLookup(Settings.Trackers.ToArray()) : Array.Empty<string>();
    }

    private string TorrentsDirectory => Path.Combine(Settings.AppDataDirectory, "torrents");
    private string DownloadedTorrentsDirectory => Path.Combine(TorrentsDirectory, "downloads");

    /// <summary>
    /// Starts the background pipeline and restores previous sessions' downloads and shares. Never
    /// throws: a failure in one part (say, the crawler's UDP port being taken) is logged and the
    /// rest keeps working, because the UI starts this without awaiting it.
    /// </summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        for (int i = 0; i < ResolverWorkerCount; i++)
            _workers.Add(Task.Run(() => ResolveWorkerAsync(token)));
        _workers.Add(Task.Run(() => FlushSightingsLoopAsync(token)));

        try
        {
            await Crawler.StartAsync(Settings.CrawlerPort, token).ConfigureAwait(false);
            Log($"DHT crawler listening on UDP {Crawler.LocalPort}");

            // Without a forwarded port, a home router drops most of the queries other nodes send
            // the crawler, and those queries are the search index's only source. Never in offline
            // (test) mode, which must not touch the real router.
            if (_enableNetwork)
            {
                _portMapper = new PortMapper(Crawler.LocalPort, Log);
                _portMapper.Start();
            }
        }
        catch (Exception ex)
        {
            Log(
                $"DHT crawler could not start on UDP {Settings.CrawlerPort} ({ex.Message}). " +
                "The search index will not grow this session; change the crawler port in Settings. Downloads and sharing still work.");
        }

        await RestoreSharesAsync().ConfigureAwait(false);
        await RestoreDownloadsAsync().ConfigureAwait(false);
    }

    private async Task RestoreSharesAsync()
    {
        string[] paths;
        lock (Settings.SyncRoot) paths = Settings.SharedPaths.ToArray();

        foreach (var path in paths)
        {
            try
            {
                await Engine.ShareAsync(path, TorrentsDirectory, TrackersSnapshot()).ConfigureAwait(false);
                Log($"Resumed seeding: {path}");
            }
            catch (Exception ex)
            {
                Log($"Could not resume seeding {path}: {ex.Message}");
            }
        }
    }

    private async Task RestoreDownloadsAsync()
    {
        SavedDownload[] saved;
        lock (Settings.SyncRoot) saved = Settings.Downloads.ToArray();

        foreach (var download in saved)
        {
            try
            {
                var snapshot = await AddToEngineAsync(DownloadSource.Parse(download.Source)).ConfigureAwait(false);
                Log($"Resumed download: {snapshot.Name}");
            }
            catch (Exception ex)
            {
                Log($"Could not resume download {download.InfoHashHex}: {ex.Message}");
            }
        }
    }

    public async Task<bool> AddSharedPathAsync(string path)
    {
        try
        {
            await Engine.ShareAsync(path, TorrentsDirectory, TrackersSnapshot()).ConfigureAwait(false);
            lock (Settings.SyncRoot)
            {
                if (!Settings.SharedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.SharedPaths.Add(path);
                    Settings.Save();
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"Could not share {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds a download from a magnet link, a bare info-hash or a .torrent file path, and remembers
    /// it so it resumes after a restart.
    /// </summary>
    /// <exception cref="ArgumentException">The input isn't something that can be downloaded.</exception>
    public async Task<TransferSnapshot> AddDownloadAsync(string input)
    {
        var source = DownloadSource.Parse(input);
        var snapshot = await AddToEngineAsync(source).ConfigureAwait(false);

        // Keep our own copy of a .torrent so the download still resumes if the original is moved.
        var persistedSource = source.Value;
        if (source.Kind == DownloadSourceKind.TorrentFile)
        {
            Directory.CreateDirectory(DownloadedTorrentsDirectory);
            persistedSource = Path.Combine(DownloadedTorrentsDirectory, snapshot.InfoHashHex.ToLowerInvariant() + ".torrent");
            if (!string.Equals(Path.GetFullPath(source.Value), Path.GetFullPath(persistedSource), StringComparison.OrdinalIgnoreCase))
                File.Copy(source.Value, persistedSource, overwrite: true);
        }

        lock (Settings.SyncRoot)
        {
            if (!Settings.Downloads.Any(d => d.InfoHashHex.Equals(snapshot.InfoHashHex, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.Downloads.Add(new SavedDownload { InfoHashHex = snapshot.InfoHashHex, Source = persistedSource });
                Settings.Save();
            }
        }
        return snapshot;
    }

    private Task<TransferSnapshot> AddToEngineAsync(DownloadSource source) => source.Kind switch
    {
        DownloadSourceKind.TorrentFile => Engine.AddTorrentFileAsync(source.Value, null, TrackersSnapshot()),
        _ => Engine.AddMagnetAsync(source.Value, null, TrackersSnapshot()),
    };

    /// <summary>
    /// Removes a transfer from the engine and forgets it: a download won't resume at the next
    /// start, and a shared item stops being re-shared. Downloaded/shared files on disk are untouched.
    /// </summary>
    public async Task RemoveTransferAsync(string infoHashHex)
    {
        var snapshot = Engine.GetSnapshots()
            .FirstOrDefault(s => s.InfoHashHex.Equals(infoHashHex, StringComparison.OrdinalIgnoreCase));
        await Engine.RemoveAsync(infoHashHex).ConfigureAwait(false);

        lock (Settings.SyncRoot)
        {
            var changed = Settings.Downloads.RemoveAll(d => d.InfoHashHex.Equals(infoHashHex, StringComparison.OrdinalIgnoreCase)) > 0;
            if (snapshot is { IsSeedOnly: true })
            {
                var sharedPath = NormalizePath(Path.Combine(snapshot.SavePath, snapshot.Name));
                changed |= Settings.SharedPaths.RemoveAll(p => NormalizePath(p).Equals(sharedPath, StringComparison.OrdinalIgnoreCase)) > 0;
            }
            if (changed) Settings.Save();
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void OnInfoHashObserved(InfoHashObservation observation)
    {
        var hex = observation.InfoHash.ToHex().ToLowerInvariant();

        bool known;
        lock (_indexedLock) known = _indexed.Contains(hex);

        if (known)
        {
            // Already searchable; just count the sighting so popular torrents rank higher.
            _pendingSightings.AddOrUpdate(hex, 1, static (_, n) => n + 1);
            return;
        }

        if (_attempted.TryAdd(hex))
            _resolutionQueue.Writer.TryWrite(hex);
    }

    private async Task FlushSightingsLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(SightingFlushInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                FlushSightings();
        }
        catch (OperationCanceledException) { }
        finally
        {
            FlushSightings();
        }
    }

    private void FlushSightings()
    {
        if (_pendingSightings.IsEmpty) return;

        var batch = new Dictionary<string, int>();
        foreach (var key in _pendingSightings.Keys)
            if (_pendingSightings.TryRemove(key, out var count))
                batch[key] = count;

        try { Index.RecordSightings(batch); }
        catch (Exception ex) { Log($"Could not record popularity counts: {ex.Message}"); }
    }

    private async Task ResolveWorkerAsync(CancellationToken token)
    {
        try
        {
            await foreach (var hex in _resolutionQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await ResolveOneAsync(hex, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad torrent (or a transient database error) must not kill the worker.
                    Log($"Could not index {hex[..Math.Min(8, hex.Length)]}...: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ResolveOneAsync(string hex, CancellationToken token)
    {
        Interlocked.Increment(ref _lookupsAttempted);
        var metadata = await Engine.TryFetchMetadataAsync(hex, MetadataTimeout, token, LookupTrackers()).ConfigureAwait(false);
        if (metadata is null) return;

        var result = Index.UpsertResolved(metadata);
        lock (_indexedLock) _indexed.Add(hex);
        Interlocked.Increment(ref _lookupsResolved);

        Log($"Indexed \"{result.Name}\" ({FormatSize(result.TotalSize)}, {result.FileCount} file(s))");
        TorrentIndexed?.Invoke(result);
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
        // Each step is isolated so that one failing can't skip the rest - in particular the index
        // must always be closed, and the workers' final popularity flush must run before that.
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _resolutionQueue.Writer.TryComplete();

        if (_portMapper is not null)
        {
            try { await _portMapper.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"Port mapping did not shut down cleanly: {ex.Message}"); }
        }

        try { await Crawler.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log($"Crawler did not stop cleanly: {ex.Message}"); }

        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Background workers did not stop cleanly: {ex.Message}"); }

        await Engine.DisposeAsync().ConfigureAwait(false);
        Index.Dispose();
    }
}
