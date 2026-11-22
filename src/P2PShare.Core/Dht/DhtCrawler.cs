using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MonoTorrent.BEncoding;

namespace P2PShare.Core.Dht;

public enum ObservationKind { GetPeers, AnnouncePeer }

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
    private static readonly (string Host, int Port)[] BootstrapNodes =
    {
        ("router.bittorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("router.utorrent.com", 6881),
    };

    private const int MaxRoutingTableSize = 1000;
    private const int ClosestNodesReturned = 8;

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

    public event Action<InfoHashObservation>? InfoHashObserved;
    public event Action<string>? Log;

    public int NodeCount => _routingTable.Count;
    public long QueriesObserved { get; private set; }
    public long UniqueInfoHashesObserved => _seenInfoHashes.Count;
    public bool IsRunning { get; private set; }

    private sealed record RoutingEntry(NodeId Id, IPEndPoint EndPoint, DateTime LastSeenUtc);

    public async Task StartAsync(int port, CancellationToken token)
    {
        if (IsRunning) return;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        IsRunning = true;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _maintenanceLoop = Task.Run(() => MaintenanceLoopAsync(_cts.Token));

        await BootstrapAsync(_cts.Token).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        _ = StopAsync();
        return ValueTask.CompletedTask;
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        foreach (var (host, port) in BootstrapNodes)
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
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
        }
    }

    private void PruneRoutingTable()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var kvp in _routingTable)
            if (kvp.Value.LastSeenUtc < cutoff)
                _routingTable.TryRemove(kvp.Key, out _);

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
                HandleResponse(dict, from);
                break;
            // "e" (error) messages are not actionable for a crawler; ignore.
        }
    }

    private void HandleQuery(BEncodedDictionary dict, BEncodedString transactionId, IPEndPoint from)
    {
        if (!dict.TryGetValue("q", out var qVal) || qVal is not BEncodedString method) return;
        if (!dict.TryGetValue("a", out var aVal) || aVal is not BEncodedDictionary args) return;

        QueriesObserved++;

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

    private void HandleResponse(BEncodedDictionary dict, IPEndPoint from)
    {
        if (!dict.TryGetValue("r", out var rVal) || rVal is not BEncodedDictionary r) return;

        if (r.TryGetValue("id", out var idVal) && idVal is BEncodedString idStr && idStr.Span.Length == NodeId.Length)
            LearnNode(new NodeId(idStr.Span.ToArray()), from);

        if (r.TryGetValue("nodes", out var nodesVal) && nodesVal is BEncodedString nodesStr)
            foreach (var (id, ep) in DecodeCompactNodes(nodesStr.Span))
                LearnNode(id, ep);
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
            InfoHashObserved?.Invoke(observation);
        else
            // Already known, but still worth telling listeners it's still alive out there.
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

    private async Task SendQueryAsync(IPEndPoint target, string method, BEncodedDictionary args, CancellationToken ct)
    {
        var message = new BEncodedDictionary
        {
            { "t", NextTransactionId() },
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
        _ = _udp?.SendAsync(bytes, to);
    }

    private BEncodedString NextTransactionId()
    {
        var bytes = new byte[2];
        _rnd.NextBytes(bytes);
        return new BEncodedString(bytes);
    }
}
