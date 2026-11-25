using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MonoTorrent.BEncoding;

namespace P2PShare.Core.Dht;

public enum ObservationKind
{
    GetPeers,
    AnnouncePeer,

    /// <summary>
    /// Handed to us by a node we asked (BEP 51 <c>sample_infohashes</c>). The torrent exists, but
    /// unlike the other two kinds this says nothing about current demand for it.
    /// </summary>
    Sample,
}

public sealed record InfoHashObservation(NodeId InfoHash, ObservationKind Kind, IPEndPoint From, int? Port);

/// <summary>
/// A minimal, self-contained Mainline DHT (BEP 5) node whose purpose is not to find peers for
/// torrents we already know about (MonoTorrent's own engine does that) but to make this process
/// discoverable *by* the public DHT, so that other nodes route us the get_peers / announce_peer
/// queries real swarms generate. Observing those queries is how open-source DHT indexers such as
/// magnetico build a search index without a central tracker: the info_hash in each query is a
/// torrent circulating somewhere on the network right now. We still answer queries addressed to us
/// (ping/find_node/get_peers/announce_peer) well enough to behave like a normal node, which is what
/// keeps other nodes willing to talk to us.
/// </summary>
public sealed class DhtCrawler : IAsyncDisposable
{
    private static readonly (string Host, int Port)[] PublicBootstrapNodes =
    {
        ("router.bittorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("dht.aelitis.com", 6881),
    };

    private const int MaxRoutingTableSize = 1000;
    private const int ClosestNodesReturned = 8;

    /// <summary>
    /// Bound on the "recently seen" info-hash filter. The public DHT carries far more distinct
    /// hashes than a desktop process should remember; the filter only exists so the unique counter
    /// below doesn't double count a hash that several nodes ask us about in quick succession.
    /// </summary>
    private const int MaxRememberedInfoHashes = 200_000;

    // BEP 51 sampling: we ask nodes for a random sample of the info-hashes they store. Because we
    // send the question, the answer comes back through any NAT - unlike get_peers/announce_peer
    // traffic, which only reaches us if our UDP port is reachable from outside.
    private const byte SampleTransactionTag = (byte)'S';
    private const int MaxSampleQueriesPerTick = 10;
    private const int MaxSamplesPerResponse = 100;
    private const int MaxSamplingStates = 100_000;
    private static readonly TimeSpan SampleReplyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnsupportedRetry = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxSampleInterval = TimeSpan.FromHours(6);

    private readonly (string Host, int Port)[] _bootstrapNodes;
    private readonly NodeId _selfId = NodeId.Random();
    private readonly ConcurrentDictionary<string, RoutingEntry> _routingTable = new();
    private readonly ConcurrentDictionary<string, byte> _seenInfoHashes = new();
    private readonly Random _rnd = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _maintenanceLoop;
    private byte[] _tokenSecret = RandomNumberGenerator.GetBytes(16);
    private byte[] _previousTokenSecret = RandomNumberGenerator.GetBytes(16);
    private long _queriesObserved;
    private long _uniqueInfoHashesObserved;
    private readonly TimeSpan _maintenanceInterval;
    private readonly ConcurrentDictionary<string, SampleState> _sampling = new();
    private long _sampleQueriesSent;
    private long _sampleReplies;
    private long _sampledInfoHashes;

    public event Action<InfoHashObservation>? InfoHashObserved;
    public event Action<string>? Log;

    public int NodeCount => _routingTable.Count;
    public long QueriesObserved => Interlocked.Read(ref _queriesObserved);
    public long UniqueInfoHashesObserved => Interlocked.Read(ref _uniqueInfoHashesObserved);
    public bool IsRunning { get; private set; }

    /// <summary>BEP 51 sampling: questions sent, replies that carried samples, and info-hashes received that way.</summary>
    public long SampleQueriesSent => Interlocked.Read(ref _sampleQueriesSent);
    public long SampleReplies => Interlocked.Read(ref _sampleReplies);
    public long SampledInfoHashes => Interlocked.Read(ref _sampledInfoHashes);

    /// <summary>The UDP port actually bound (differs from the requested one when 0 was asked for). 0 when stopped.</summary>
    public int LocalPort => (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    private sealed record RoutingEntry(NodeId Id, IPEndPoint EndPoint, DateTime LastSeenUtc);

    /// <summary>Per remote node: when we may ask it for samples again, and whether a question is in flight.</summary>
    private sealed class SampleState
    {
        public DateTime NextUtc;
        public DateTime? PendingSince;
    }

    /// <param name="bootstrapNodes">
    /// Hosts to contact to join the network. Defaults to the well-known public routers; pass an
    /// empty list to run isolated (used by tests, which must not touch the real network).
    /// </param>
    /// <param name="maintenanceInterval">How often the crawler does its housekeeping and sends its
    /// own queries. Defaults to four seconds; tests shorten it.</param>
    public DhtCrawler(IEnumerable<(string Host, int Port)>? bootstrapNodes = null, TimeSpan? maintenanceInterval = null)
    {
        _bootstrapNodes = bootstrapNodes?.ToArray() ?? PublicBootstrapNodes;
        _maintenanceInterval = maintenanceInterval ?? TimeSpan.FromSeconds(4);
    }

    public async Task StartAsync(int port, CancellationToken token)
    {
        if (IsRunning) return;

        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            DisableUdpConnectionReset(udp.Client);
        }
        catch
        {
            // Don't leak the socket when the port is already taken.
            udp.Dispose();
            throw;
        }

        _udp = udp;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        IsRunning = true;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _maintenanceLoop = Task.Run(() => MaintenanceLoopAsync(_cts.Token));

        await BootstrapAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// On Windows an ICMP "port unreachable" reply to one of our datagrams makes the *next*
    /// receive on the socket fail with a reset, which for a node that sends to thousands of dead
    /// addresses means a constant stream of spurious errors. Turning that off is the standard fix.
    /// </summary>
    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SioUdpConnReset = -1744830452;
        try { socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* not fatal; the receive loop tolerates the errors */ }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try
        {
            if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false);
            if (_maintenanceLoop is not null) await _maintenanceLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _udp?.Dispose();
        _udp = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task BootstrapAsync(CancellationToken ct)
    {
        foreach (var (host, port) in _bootstrapNodes)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                if (addresses.Length == 0) continue;
                var ep = new IPEndPoint(addresses[0], port);
                await SendFindNodeAsync(ep, _selfId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Bootstrap to {host} failed: {ex.Message}");
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_maintenanceInterval);
        var lastSecretRotation = DateTime.UtcNow;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (DateTime.UtcNow - lastSecretRotation > TimeSpan.FromMinutes(5))
            {
                _previousTokenSecret = _tokenSecret;
                _tokenSecret = RandomNumberGenerator.GetBytes(16);
                lastSecretRotation = DateTime.UtcNow;
            }

            PruneRoutingTable();

            if (_routingTable.IsEmpty)
            {
                await BootstrapAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Random-walk fan-out: ask a handful of known nodes about random targets. This both
            // discovers new nodes (growing our visibility into the swarm) and, as a side effect,
            // puts our address in front of more of the network so it routes real traffic to us.
            foreach (var entry in PickRandom(_routingTable.Values, 5))
            {
                var target = NodeId.RandomCloseTo(entry.Id);
                await SendFindNodeAsync(entry.EndPoint, target, ct).ConfigureAwait(false);
            }

            await SampleSomeNodesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks a few nodes that are due for a BEP 51 sample. Most nodes don't implement it, so a node
    /// that doesn't answer is left alone for hours; one that does tells us how long to wait.
    /// </summary>
    private async Task SampleSomeNodesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var state in _sampling.Values)
        {
            lock (state)
            {
                if (state.PendingSince is { } since && now - since > SampleReplyTimeout)
                {
                    state.PendingSince = null;
                    state.NextUtc = now + UnsupportedRetry;
                }
            }
        }

        int sent = 0;
        foreach (var entry in _routingTable.Values.OrderBy(_ => Random.Shared.Next()))
        {
            if (sent >= MaxSampleQueriesPerTick || ct.IsCancellationRequested) break;

            var state = _sampling.GetOrAdd(entry.EndPoint.ToString(), static _ => new SampleState());
            lock (state)
            {
                if (state.PendingSince is not null || state.NextUtc > now) continue;
                state.PendingSince = now;
            }

            Interlocked.Increment(ref _sampleQueriesSent);
            await SendSampleQueryAsync(entry.EndPoint, NodeId.Random(), ct).ConfigureAwait(false);
            sent++;
        }
    }

    private void PruneRoutingTable()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var kvp in _routingTable)
            if (kvp.Value.LastSeenUtc < cutoff)
                _routingTable.TryRemove(kvp.Key, out _);

        // Forget only the sampling bookkeeping that no longer constrains us (nothing in flight, no
        // waiting period left); the rest has to stay so we keep honouring each node's interval.
        if (_sampling.Count > MaxSamplingStates)
        {
            _sampling.Clear();
        }
        else if (_sampling.Count > MaxRoutingTableSize * 5)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _sampling)
                lock (kvp.Value)
                    if (kvp.Value.PendingSince is null && kvp.Value.NextUtc <= now)
                        _sampling.TryRemove(kvp.Key, out _);
        }

        if (_routingTable.Count <= MaxRoutingTableSize) return;
        foreach (var key in _routingTable.OrderBy(k => k.Value.LastSeenUtc).Take(_routingTable.Count - MaxRoutingTableSize).Select(k => k.Key))
            _routingTable.TryRemove(key, out _);
    }

    private static IEnumerable<RoutingEntry> PickRandom(ICollection<RoutingEntry> source, int count)
    {
        if (source.Count == 0) yield break;
        var list = source.ToArray();
        var rnd = Random.Shared;
        for (int i = 0; i < Math.Min(count, list.Length); i++)
            yield return list[rnd.Next(list.Length)];
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try
            {
                HandleDatagram(result.Buffer, result.RemoteEndPoint);
            }
            catch
            {
                // Malformed / non-KRPC datagram (the public DHT has plenty) - ignore and keep listening.
            }
        }
    }

    private void HandleDatagram(byte[] buffer, IPEndPoint from)
    {
        var dict = BEncodedValue.Decode<BEncodedDictionary>(buffer);
        if (!dict.TryGetValue("y", out var yVal) || yVal is not BEncodedString y) return;
        var transactionId = dict.TryGetValue("t", out var t) ? (BEncodedString)t : new BEncodedString("");

        switch (y.Text)
        {
            case "q":
                HandleQuery(dict, transactionId, from);
                break;
            case "r":
                HandleResponse(dict, transactionId, from);
                break;
            // "e" (error) messages are not actionable for a crawler; ignore.
        }
    }

    private void HandleQuery(BEncodedDictionary dict, BEncodedString transactionId, IPEndPoint from)
    {
        if (!dict.TryGetValue("q", out var qVal) || qVal is not BEncodedString method) return;
        if (!dict.TryGetValue("a", out var aVal) || aVal is not BEncodedDictionary args) return;

        Interlocked.Increment(ref _queriesObserved);

        if (args.TryGetValue("id", out var idVal) && idVal is BEncodedString idStr && idStr.Span.Length == NodeId.Length)
            LearnNode(new NodeId(idStr.Span.ToArray()), from);

        switch (method.Text)
        {
            case "ping":
                SendReply(from, transactionId, new BEncodedDictionary { { "id", SelfIdValue() } });
                break;

            case "find_node":
                SendReply(from, transactionId, new BEncodedDictionary
                {
                    { "id", SelfIdValue() },
                    { "nodes", EncodeCompactNodes(ClosestKnownNodes(TargetOrSelf(args, "target"))) },
                });
                break;

            case "get_peers":
                if (TryReadInfoHash(args, out var getPeersHash))
                    RaiseObservation(new InfoHashObservation(getPeersHash, ObservationKind.GetPeers, from, null));
                SendReply(from, transactionId, new BEncodedDictionary
                {
                    { "id", SelfIdValue() },
                    { "token", MakeToken(from.Address) },
                    { "nodes", EncodeCompactNodes(ClosestKnownNodes(TargetOrSelf(args, "info_hash"))) },
                });
                break;

            case "announce_peer":
                if (TryReadInfoHash(args, out var announceHash))
                {
                    int port = from.Port;
                    if (args.TryGetValue("port", out var portVal) && portVal is BEncodedNumber portNum
                        && !(args.TryGetValue("implied_port", out var implied) && implied is BEncodedNumber { Number: 1 }))
                        port = (int)portNum.Number;
                    RaiseObservation(new InfoHashObservation(announceHash, ObservationKind.AnnouncePeer, from, port));
                }
                SendReply(from, transactionId, new BEncodedDictionary { { "id", SelfIdValue() } });
                break;
        }
    }

    private void HandleResponse(BEncodedDictionary dict, BEncodedString transactionId, IPEndPoint from)
    {
        if (!dict.TryGetValue("r", out var rVal) || rVal is not BEncodedDictionary r) return;

        if (r.TryGetValue("id", out var idVal) && idVal is BEncodedString idStr && idStr.Span.Length == NodeId.Length)
            LearnNode(new NodeId(idStr.Span.ToArray()), from);

        if (r.TryGetValue("nodes", out var nodesVal) && nodesVal is BEncodedString nodesStr)
            foreach (var (id, ep) in DecodeCompactNodes(nodesStr.Span))
                if (IsRoutable(ep))
                    LearnNode(id, ep);

        // BEP 51: a node that supports sampling always includes "samples", even when empty. A reply
        // to our sampling question that lacks it comes from a node that doesn't.
        if (r.TryGetValue("samples", out var samplesVal) && samplesVal is BEncodedString samples)
            HandleSamples(r, samples, from);
        else if (transactionId.Span.Length == 2 && transactionId.Span[0] == SampleTransactionTag)
            MarkSamplingUnsupported(from);
    }

    private void HandleSamples(BEncodedDictionary r, BEncodedString samples, IPEndPoint from)
    {
        // Believe a reply only if we asked this very address a moment ago. Otherwise anyone could
        // push made-up hashes into the metadata queue.
        if (!_sampling.TryGetValue(from.ToString(), out var state)) return;

        var interval = DefaultSampleInterval;
        if (r.TryGetValue("interval", out var intervalVal) && intervalVal is BEncodedNumber seconds)
            interval = TimeSpan.FromSeconds(Math.Clamp(seconds.Number, 0, (long)MaxSampleInterval.TotalSeconds));
        if (interval < MinSampleInterval) interval = MinSampleInterval;

        lock (state)
        {
            if (state.PendingSince is null) return;
            state.PendingSince = null;
            state.NextUtc = DateTime.UtcNow + interval;
        }

        Interlocked.Increment(ref _sampleReplies);
        var span = samples.Span;
        int count = Math.Min(span.Length / NodeId.Length, MaxSamplesPerResponse);
        for (int i = 0; i < count; i++)
        {
            var hash = new NodeId(span.Slice(i * NodeId.Length, NodeId.Length).ToArray());
            Interlocked.Increment(ref _sampledInfoHashes);
            RaiseObservation(new InfoHashObservation(hash, ObservationKind.Sample, from, null));
        }
    }

    private void MarkSamplingUnsupported(IPEndPoint from)
    {
        if (!_sampling.TryGetValue(from.ToString(), out var state)) return;
        lock (state)
        {
            if (state.PendingSince is null) return;
            state.PendingSince = null;
            state.NextUtc = DateTime.UtcNow + UnsupportedRetry;
        }
    }

    /// <summary>
    /// Node lists in responses are untrusted. Skip entries that could never be a real remote node
    /// (port 0, unspecified/loopback/multicast/broadcast addresses) so a hostile or buggy peer
    /// can't get us to fire find_node traffic at arbitrary local or nonsensical targets.
    /// </summary>
    private static bool IsRoutable(IPEndPoint endPoint)
    {
        if (endPoint.Port == 0) return false;
        var bytes = endPoint.Address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224) return false;
        return true;
    }

    private static bool TryReadInfoHash(BEncodedDictionary args, out NodeId infoHash)
    {
        if (args.TryGetValue("info_hash", out var v) && v is BEncodedString s && s.Span.Length == NodeId.Length)
        {
            infoHash = new NodeId(s.Span.ToArray());
            return true;
        }
        infoHash = default;
        return false;
    }

    private NodeId TargetOrSelf(BEncodedDictionary args, string key)
    {
        if (args.TryGetValue(key, out var v) && v is BEncodedString s && s.Span.Length == NodeId.Length)
            return new NodeId(s.Span.ToArray());
        return _selfId;
    }

    private void RaiseObservation(InfoHashObservation observation)
    {
        if (_seenInfoHashes.TryAdd(observation.InfoHash.ToHex(), 0))
        {
            Interlocked.Increment(ref _uniqueInfoHashesObserved);
            if (_seenInfoHashes.Count > MaxRememberedInfoHashes)
                _seenInfoHashes.Clear();
        }

        // Every observation is reported, not just the first: a hash that keeps being asked about
        // is a popular one, which is what the search index uses to rank results.
        InfoHashObserved?.Invoke(observation);
    }

    private void LearnNode(NodeId id, IPEndPoint endPoint)
    {
        if (id.Equals(_selfId)) return;
        _routingTable[endPoint.ToString()] = new RoutingEntry(id, endPoint, DateTime.UtcNow);
    }

    private IEnumerable<(NodeId Id, IPEndPoint EndPoint)> ClosestKnownNodes(NodeId target)
    {
        return _routingTable.Values
            .OrderBy(e => XorDistance(e.Id, target), ByteArrayComparer.Instance)
            .Take(ClosestNodesReturned)
            .Select(e => (e.Id, e.EndPoint));
    }

    private static byte[] XorDistance(NodeId a, NodeId b)
    {
        var result = new byte[NodeId.Length];
        var sa = a.Span;
        var sb = b.Span;
        for (int i = 0; i < NodeId.Length; i++) result[i] = (byte)(sa[i] ^ sb[i]);
        return result;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            for (int i = 0; i < x!.Length; i++)
            {
                int c = x[i].CompareTo(y![i]);
                if (c != 0) return c;
            }
            return 0;
        }
    }

    private BEncodedString MakeToken(IPAddress address)
    {
        using var hmac = new HMACSHA1(_tokenSecret);
        return new BEncodedString(hmac.ComputeHash(address.GetAddressBytes())[..8]);
    }

    private BEncodedString SelfIdValue() => new(_selfId.ToBytes());

    private static IEnumerable<(NodeId Id, IPEndPoint EndPoint)> DecodeCompactNodes(ReadOnlySpan<byte> data)
    {
        const int entrySize = 26; // 20 byte id + 4 byte IPv4 + 2 byte port
        var list = new List<(NodeId, IPEndPoint)>(data.Length / entrySize);
        for (int offset = 0; offset + entrySize <= data.Length; offset += entrySize)
        {
            var id = new NodeId(data.Slice(offset, NodeId.Length).ToArray());
            var ipBytes = data.Slice(offset + NodeId.Length, 4);
            var port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + NodeId.Length + 4, 2));
            var ep = new IPEndPoint(new IPAddress(ipBytes), port);
            list.Add((id, ep));
        }
        return list;
    }

    private static BEncodedString EncodeCompactNodes(IEnumerable<(NodeId Id, IPEndPoint EndPoint)> nodes)
    {
        using var ms = new MemoryStream();
        Span<byte> portBytes = stackalloc byte[2];
        foreach (var (id, ep) in nodes)
        {
            ms.Write(id.ToBytes());
            ms.Write(ep.Address.MapToIPv4().GetAddressBytes());
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)ep.Port);
            ms.Write(portBytes);
        }
        return new BEncodedString(ms.ToArray());
    }

    private async Task SendFindNodeAsync(IPEndPoint target, NodeId searchTarget, CancellationToken ct)
    {
        var args = new BEncodedDictionary { { "id", SelfIdValue() }, { "target", new BEncodedString(searchTarget.ToBytes()) } };
        await SendQueryAsync(target, "find_node", args, ct).ConfigureAwait(false);
    }

    private async Task SendSampleQueryAsync(IPEndPoint target, NodeId searchTarget, CancellationToken ct)
    {
        // "target" only steers which close nodes come back (so each question also walks the
        // keyspace); it has no effect on which samples a node returns.
        var args = new BEncodedDictionary { { "id", SelfIdValue() }, { "target", new BEncodedString(searchTarget.ToBytes()) } };
        var transactionId = new BEncodedString(new[] { SampleTransactionTag, (byte)_rnd.Next(256) });
        await SendQueryAsync(target, "sample_infohashes", args, ct, transactionId).ConfigureAwait(false);
    }

    private async Task SendQueryAsync(
        IPEndPoint target, string method, BEncodedDictionary args, CancellationToken ct, BEncodedString? transactionId = null)
    {
        var message = new BEncodedDictionary
        {
            { "t", transactionId ?? NextTransactionId() },
            { "y", new BEncodedString("q") },
            { "q", new BEncodedString(method) },
            { "a", args },
        };
        var bytes = message.Encode();
        try
        {
            await _udp!.SendAsync(bytes, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Send to {target} failed: {ex.Message}");
        }
    }

    private void SendReply(IPEndPoint to, BEncodedString transactionId, BEncodedDictionary returnValues)
    {
        var message = new BEncodedDictionary
        {
            { "t", transactionId },
            { "y", new BEncodedString("r") },
            { "r", returnValues },
        };
        var bytes = message.Encode();
        _ = SendDatagramAsync(bytes, to);
    }

    private async Task SendDatagramAsync(byte[] bytes, IPEndPoint to)
    {
        try
        {
            var udp = _udp;
            if (udp is not null) await udp.SendAsync(bytes, to).ConfigureAwait(false);
        }
        catch
        {
            // Replies are best-effort UDP; a failed send (including during shutdown) is not actionable.
        }
    }

    private BEncodedString NextTransactionId()
    {
        var bytes = new byte[2];
        _rnd.NextBytes(bytes);
        // The tag marks transaction ids of our sampling questions; keep ordinary ones out of that space.
        if (bytes[0] == SampleTransactionTag) bytes[0] ^= 1;
        return new BEncodedString(bytes);
    }
}
