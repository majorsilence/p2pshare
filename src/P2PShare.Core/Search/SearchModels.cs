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
    string Magnet);
