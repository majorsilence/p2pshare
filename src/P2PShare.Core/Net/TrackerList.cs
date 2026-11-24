namespace P2PShare.Core.Net;

/// <summary>A line of the tracker list that was not accepted, and why.</summary>
public sealed record RejectedTracker(string Line, string Reason);

public sealed record TrackerParseResult(IReadOnlyList<string> Valid, IReadOnlyList<RejectedTracker> Rejected);

/// <summary>
/// The announce URLs P2PShare adds to downloads, to the torrents it creates and (optionally) to
/// search lookups. Trackers are a speed-up on top of the DHT, never a requirement: an empty list is
/// valid and simply means "DHT only".
/// </summary>
public static class TrackerList
{
    public const int MaxTrackers = 32;

    /// <summary>A background lookup asks only this many trackers, so thousands of lookups don't all hit the same few hosts.</summary>
    public const int MaxPerLookup = 4;

    /// <summary>
    /// Long-running public trackers. Each answered a UDP "connect" request (BEP 15) when this list
    /// was assembled, but public trackers come and go, so the list is meant to be edited.
    /// </summary>
    public static IReadOnlyList<string> Defaults { get; } = new[]
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://explodie.org:6969/announce",
    };

    /// <summary>Accepts udp://host:port, http://host[:port] and https://host[:port] URLs.</summary>
    public static bool TryNormalize(string? text, out string url, out string reason)
    {
        url = "";
        reason = "";
        var candidate = text?.Trim() ?? "";
        if (candidate.Length == 0) { reason = "empty"; return false; }
        if (candidate.Any(char.IsControl)) { reason = "contains control characters"; return false; }
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) { reason = "not a valid URL"; return false; }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("udp" or "http" or "https")) { reason = "must start with udp://, http:// or https://"; return false; }
        if (string.IsNullOrEmpty(uri.Host)) { reason = "has no host name"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { reason = "must not contain a user name or password"; return false; }

        // Uri reports -1 for a udp:// URL without a port, and a UDP tracker has no default one.
        if (scheme == "udp" && uri.IsDefaultPort) { reason = "udp trackers need a port, e.g. udp://host:1337/announce"; return false; }
        if (uri.Port is < -1 or 0 or > 65535) { reason = "port out of range"; return false; }

        url = candidate;
        return true;
    }

    /// <summary>
    /// Reads one URL per line. Blank lines and lines starting with # are ignored; repeats are
    /// dropped; anything unusable is reported rather than silently lost.
    /// </summary>
    public static TrackerParseResult Parse(string? text)
    {
        var valid = new List<string>();
        var rejected = new List<RejectedTracker>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (!TryNormalize(line, out var url, out var reason))
            {
                rejected.Add(new RejectedTracker(line, reason));
            }
            else if (valid.Count >= MaxTrackers)
            {
                rejected.Add(new RejectedTracker(line, $"more than {MaxTrackers} trackers"));
            }
            else if (seen.Add(url))
            {
                valid.Add(url);
            }
        }

        return new TrackerParseResult(valid, rejected);
    }

    public static string ToText(IEnumerable<string> urls) => string.Join(Environment.NewLine, urls);

    /// <summary>The first list's entries followed by any of the second's that it doesn't already have.</summary>
    public static List<string> Merge(IEnumerable<string>? first, IEnumerable<string>? extra)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in (first ?? Enumerable.Empty<string>()).Concat(extra ?? Enumerable.Empty<string>()))
        {
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url)) merged.Add(url);
        }
        return merged;
    }

    /// <summary>All trackers when there are few, otherwise a random handful.</summary>
    public static IReadOnlyList<string> PickForLookup(IReadOnlyList<string> trackers, Random? random = null)
    {
        if (trackers.Count <= MaxPerLookup) return trackers;
        random ??= Random.Shared;
        return trackers.OrderBy(_ => random.Next()).Take(MaxPerLookup).ToList();
    }
}
