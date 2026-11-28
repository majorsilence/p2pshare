using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MonoTorrent.BEncoding;

namespace P2PShare.Core.Net;

/// <summary>What a tracker says about one torrent.</summary>
public readonly record struct ScrapeCounts(int Seeders, int Completed, int Leechers);

/// <summary>
/// Asks trackers how many peers they know for a batch of torrents ("scrape"), over UDP (BEP 15) or
/// HTTP (BEP 48). A scrape only reads counters: it does not join the swarm or announce us as a peer.
///
/// Trackers see overlapping sets of peers, so counts are not added up across them; each torrent gets
/// the highest figure any tracker reported. Every failure - dead tracker, garbage reply, timeout -
/// just means that tracker contributes nothing.
/// </summary>
public sealed class TrackerScraper : IDisposable
{
    /// <summary>BEP 15 allows about 74 hashes in one UDP request; stay a little under.</summary>
    public const int MaxHashesPerUdpRequest = 70;

    /// <summary>HTTP puts the hashes in the URL, which servers limit in length.</summary>
    public const int MaxHashesPerHttpRequest = 40;

    private const long UdpProtocolId = 0x41727101980;
    private const int ActionConnect = 0;
    private const int ActionScrape = 2;
    private const int MaxHttpResponseBytes = 256 * 1024;
    private const int MaxCount = 10_000_000; // anything beyond this is nonsense

    private readonly TimeSpan _timeout;
    private readonly int _udpAttempts;
    private readonly HttpClient _http;

    public TrackerScraper(TimeSpan? timeout = null, int udpAttempts = 2)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _udpAttempts = Math.Max(1, udpAttempts);
        _http = new HttpClient { Timeout = _timeout, MaxResponseContentBufferSize = MaxHttpResponseBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("P2PShare/1.0");
    }

    /// <summary>
    /// Returns counts for the torrents (40-character hex info-hashes) that at least one tracker
    /// answered about; keys are lower-case. A torrent no tracker answered for is absent, which is
    /// different from a torrent that trackers report as having no peers.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ScrapeCounts>> ScrapeAsync(
        IReadOnlyList<string> trackers, IReadOnlyList<string> infoHashesHex, CancellationToken token)
    {
        var hashes = infoHashesHex.Select(h => h.ToLowerInvariant()).Distinct().ToList();
        var merged = new Dictionary<string, ScrapeCounts>();
        if (trackers.Count == 0 || hashes.Count == 0) return merged;

        var binary = hashes.Select(Convert.FromHexString).ToList();
        var replies = await Task.WhenAll(trackers.Select(t => ScrapeOneAsync(t, binary, token))).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        foreach (var reply in replies)
        {
            foreach (var (index, counts) in reply)
            {
                var hash = hashes[index];
                merged[hash] = merged.TryGetValue(hash, out var existing)
                    ? new ScrapeCounts(
                        Math.Max(existing.Seeders, counts.Seeders),
                        Math.Max(existing.Completed, counts.Completed),
                        Math.Max(existing.Leechers, counts.Leechers))
                    : counts;
            }
        }

        return merged;
    }

    private async Task<List<(int Index, ScrapeCounts Counts)>> ScrapeOneAsync(
        string tracker, IReadOnlyList<byte[]> hashes, CancellationToken token)
    {
        var results = new List<(int, ScrapeCounts)>();
        try
        {
            if (!Uri.TryCreate(tracker, UriKind.Absolute, out var uri)) return results;
            if (uri.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase))
                await ScrapeUdpAsync(uri, hashes, results, token).ConfigureAwait(false);
            else if (uri.Scheme is "http" or "https")
                await ScrapeHttpAsync(tracker, hashes, results, token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Public trackers come and go; keep whatever arrived before the failure.
        }
        catch (Exception)
        {
            // Cancelled: the caller notices through the token.
        }

        return results;
    }

    // ---- UDP (BEP 15) -----------------------------------------------------------------------

    private async Task ScrapeUdpAsync(
        Uri tracker, IReadOnlyList<byte[]> hashes, List<(int, ScrapeCounts)> results, CancellationToken token)
    {
        // Name lookup gets the same patience as a datagram; a resolver that sits on a dead tracker's
        // name must not hold up the others.
        var addresses = await Dns.GetHostAddressesAsync(tracker.Host, AddressFamily.InterNetwork, token)
            .WaitAsync(_timeout, token).ConfigureAwait(false);
        if (addresses.Length == 0) return;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPEndPoint(addresses[0], tracker.Port)); // only this tracker's datagrams are received

        var connect = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(connect, UdpProtocolId);
        BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(8), ActionConnect);
        var reply = await ExchangeAsync(udp, connect, transactionOffset: 12, ActionConnect, minimumLength: 16, token).ConfigureAwait(false);
        if (reply is null) return;
        long connectionId = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(8));

        for (int start = 0; start < hashes.Count; start += MaxHashesPerUdpRequest)
        {
            int count = Math.Min(MaxHashesPerUdpRequest, hashes.Count - start);
            var request = new byte[16 + 20 * count];
            BinaryPrimitives.WriteInt64BigEndian(request, connectionId);
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), ActionScrape);
            for (int i = 0; i < count; i++) hashes[start + i].CopyTo(request, 16 + 20 * i);

            var answer = await ExchangeAsync(udp, request, transactionOffset: 12, ActionScrape, minimumLength: 8, token).ConfigureAwait(false);
            if (answer is null) return;

            // 12 bytes per torrent, in the order asked: seeders, completed, leechers.
            int available = Math.Min(count, (answer.Length - 8) / 12);
            for (int i = 0; i < available; i++)
            {
                var entry = answer.AsSpan(8 + 12 * i, 12);
                results.Add((start + i, new ScrapeCounts(
                    Clamp(BinaryPrimitives.ReadInt32BigEndian(entry)),
                    Clamp(BinaryPrimitives.ReadInt32BigEndian(entry[4..])),
                    Clamp(BinaryPrimitives.ReadInt32BigEndian(entry[8..])))));
            }
        }
    }

    /// <summary>Sends a request with a fresh transaction id and waits for the matching reply; null if none came.</summary>
    private async Task<byte[]?> ExchangeAsync(
        UdpClient udp, byte[] request, int transactionOffset, int expectedAction, int minimumLength, CancellationToken token)
    {
        int transaction = RandomNumberGenerator.GetInt32(int.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(transactionOffset), transaction);

        for (int attempt = 0; attempt < _udpAttempts; attempt++)
        {
            await udp.SendAsync(request, token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_timeout);
            try
            {
                while (true)
                {
                    var data = (await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false)).Buffer;
                    if (data.Length < 8 || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)) != transaction)
                        continue; // stray or forged: keep waiting for ours

                    // Right transaction but not what we asked for (action 3 is an error report).
                    return BinaryPrimitives.ReadInt32BigEndian(data) == expectedAction && data.Length >= minimumLength ? data : null;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timed out; UDP has no retransmission of its own, so send again.
            }
        }

        return null;
    }

    // ---- HTTP (BEP 48) ----------------------------------------------------------------------

    /// <summary>
    /// The scrape URL is the announce URL with "announce" in the last path segment replaced by
    /// "scrape". A tracker whose URL doesn't follow that pattern has no scrape address.
    /// </summary>
    internal static string? ScrapeUrlFor(string announceUrl)
    {
        var queryStart = announceUrl.IndexOf('?');
        var path = queryStart < 0 ? announceUrl : announceUrl[..queryStart];
        var query = queryStart < 0 ? "" : announceUrl[queryStart..];

        var slash = path.LastIndexOf('/');
        if (slash < 0 || !path[(slash + 1)..].StartsWith("announce", StringComparison.Ordinal)) return null;
        return path[..(slash + 1)] + "scrape" + path[(slash + 1 + "announce".Length)..] + query;
    }

    private async Task ScrapeHttpAsync(
        string announceUrl, IReadOnlyList<byte[]> hashes, List<(int, ScrapeCounts)> results, CancellationToken token)
    {
        var scrapeUrl = ScrapeUrlFor(announceUrl);
        if (scrapeUrl is null) return;

        for (int start = 0; start < hashes.Count; start += MaxHashesPerHttpRequest)
        {
            int count = Math.Min(MaxHashesPerHttpRequest, hashes.Count - start);
            var url = new StringBuilder(scrapeUrl);
            var separator = scrapeUrl.Contains('?') ? '&' : '?';
            for (int i = 0; i < count; i++)
            {
                url.Append(separator).Append("info_hash=");
                foreach (var b in hashes[start + i]) url.Append('%').Append(b.ToString("x2"));
                separator = '&';
            }

            // Every byte is already percent-encoded; don't let Uri "tidy" any of them.
            var target = new Uri(url.ToString(), new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
            var body = await _http.GetByteArrayAsync(target, token).ConfigureAwait(false);

            var response = BEncodedValue.Decode<BEncodedDictionary>(body);
            if (!response.TryGetValue("files", out var filesValue) || filesValue is not BEncodedDictionary files)
                return; // a "failure reason" reply, or something that isn't a scrape response

            for (int i = 0; i < count; i++)
            {
                // The keys are the raw 20-byte hashes.
                if (files.TryGetValue(new BEncodedString(hashes[start + i]), out var entry) && entry is BEncodedDictionary d)
                {
                    results.Add((start + i, new ScrapeCounts(Number(d, "complete"), Number(d, "downloaded"), Number(d, "incomplete"))));
                }
                else
                {
                    // Left out of a successful reply: the tracker doesn't know it.
                    results.Add((start + i, default));
                }
            }
        }
    }

    private static int Number(BEncodedDictionary dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) && value is BEncodedNumber n ? Clamp(n.Number) : 0;

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, MaxCount);

    public void Dispose() => _http.Dispose();
}
