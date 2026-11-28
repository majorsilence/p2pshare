namespace P2PShare.Core.Search;

public sealed record IndexedFile(string Path, long Length);

public sealed record SearchResult(
    string InfoHashHex,
    string Name,
    long TotalSize,
    int FileCount,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int SeenCount,
    string Magnet,
    ContentCategory Category = ContentCategory.Other,
    // From tracker scrapes; null until a tracker has answered for this torrent. Zero means trackers
    // know no peers, which doesn't rule out finding some over the DHT.
    int? Seeders = null,
    int? Leechers = null);
