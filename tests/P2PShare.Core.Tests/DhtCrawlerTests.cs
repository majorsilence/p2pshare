using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent.BEncoding;
using P2PShare.Core.Dht;

namespace P2PShare.Core.Tests;

/// <summary>
/// Drives a real <see cref="DhtCrawler"/> over loopback UDP with hand-built KRPC messages, the way
/// other DHT nodes would. No bootstrap hosts are configured, so nothing here touches the internet.
/// </summary>
public class DhtCrawlerTests : IAsyncLifetime
{
    private readonly DhtCrawler _crawler = new(bootstrapNodes: Array.Empty<(string, int)>());
    private readonly ConcurrentQueue<InfoHashObservation> _observed = new();
    private readonly List<UdpClient> _clients = new();

    public async ValueTask InitializeAsync()
    {
        _crawler.InfoHashObserved += _observed.Enqueue;
        await _crawler.StartAsync(0, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        await _crawler.StopAsync();
    }

    private UdpClient NewClient()
    {
        var client = Net.NewLoopbackClient();
        _clients.Add(client);
        return client;
    }

    private static BEncodedString Id(byte fill) => new(Net.Bytes(20, fill));

    [Fact]
    public void Starting_on_port_zero_picks_a_free_port()
    {
        Assert.True(_crawler.IsRunning);
        Assert.InRange(_crawler.LocalPort, 1, 65535);
    }

    [Fact]
    public async Task Ping_is_answered_with_our_node_id()
    {
        using var client = NewClient();

        var reply = await Net.RoundTripAsync(client, _crawler.LocalPort,
            Net.KrpcQuery("ping", new BEncodedDictionary { { "id", Id(1) } }, transactionId: "p1"));

        Assert.Equal("r", ((BEncodedString)reply["y"]).Text);
        Assert.Equal("p1", ((BEncodedString)reply["t"]).Text);
        var r = (BEncodedDictionary)reply["r"];
        Assert.Equal(20, ((BEncodedString)r["id"]).Span.Length);
    }

    [Fact]
    public async Task Get_peers_reports_the_info_hash_and_replies_with_a_token()
    {
        using var client = NewClient();
        var hash = new NodeId(Net.Bytes(20, 0xAB));

        var reply = await Net.RoundTripAsync(client, _crawler.LocalPort, Net.KrpcQuery("get_peers",
            new BEncodedDictionary { { "id", Id(1) }, { "info_hash", new BEncodedString(hash.ToBytes()) } }));

        var observation = Assert.Single(_observed);
        Assert.Equal(hash, observation.InfoHash);
        Assert.Equal(ObservationKind.GetPeers, observation.Kind);
        Assert.Null(observation.Port);

        var r = (BEncodedDictionary)reply["r"];
        Assert.True(r.ContainsKey("token"));
        Assert.True(r.ContainsKey("nodes"));
    }

    [Fact]
    public async Task Announce_peer_reports_the_announced_port()
    {
        using var client = NewClient();
        var hash = new NodeId(Net.Bytes(20, 0xCD));

        await Net.RoundTripAsync(client, _crawler.LocalPort, Net.KrpcQuery("announce_peer", new BEncodedDictionary
        {
            { "id", Id(2) },
            { "info_hash", new BEncodedString(hash.ToBytes()) },
            { "port", new BEncodedNumber(6881) },
            { "token", new BEncodedString("tok") },
        }));

        var observation = Assert.Single(_observed);
        Assert.Equal(hash, observation.InfoHash);
        Assert.Equal(ObservationKind.AnnouncePeer, observation.Kind);
        Assert.Equal(6881, observation.Port);
    }

    [Fact]
    public async Task Announce_with_implied_port_uses_the_senders_own_port()
    {
        using var client = NewClient();
        var senderPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        await Net.RoundTripAsync(client, _crawler.LocalPort, Net.KrpcQuery("announce_peer", new BEncodedDictionary
        {
            { "id", Id(2) },
            { "info_hash", new BEncodedString(Net.Bytes(20, 0xEF)) },
            { "port", new BEncodedNumber(1) },
            { "implied_port", new BEncodedNumber(1) },
            { "token", new BEncodedString("tok") },
        }));

        Assert.Equal(senderPort, Assert.Single(_observed).Port);
    }

    [Fact]
    public async Task Repeat_queries_are_all_reported_but_counted_as_one_unique_hash()
    {
        using var client = NewClient();
        var query = Net.KrpcQuery("get_peers", new BEncodedDictionary
        {
            { "id", Id(1) }, { "info_hash", new BEncodedString(Net.Bytes(20, 0x11)) },
        });

        await Net.RoundTripAsync(client, _crawler.LocalPort, query);
        await Net.RoundTripAsync(client, _crawler.LocalPort, query);
        await Net.RoundTripAsync(client, _crawler.LocalPort, query);

        Assert.Equal(3, _observed.Count);
        Assert.Equal(1, _crawler.UniqueInfoHashesObserved);
        Assert.Equal(3, _crawler.QueriesObserved);
    }

    [Fact]
    public async Task Garbage_datagrams_do_not_stop_the_receive_loop()
    {
        using var client = NewClient();
        var target = new IPEndPoint(IPAddress.Loopback, _crawler.LocalPort);

        await client.SendAsync(new byte[] { 0x00, 0xFF, 0x13, 0x37 }, target);
        await client.SendAsync("d1:y1:q1:q4:ping1:t2:aae"u8.ToArray(), target); // query with no arguments
        await client.SendAsync("i42e"u8.ToArray(), target);                       // valid bencode, not a dictionary

        var reply = await Net.RoundTripAsync(client, _crawler.LocalPort,
            Net.KrpcQuery("ping", new BEncodedDictionary { { "id", Id(3) } }));

        Assert.Equal("r", ((BEncodedString)reply["y"]).Text);
    }

    [Fact]
    public async Task Queries_with_a_malformed_info_hash_are_not_reported()
    {
        using var client = NewClient();

        var reply = await Net.RoundTripAsync(client, _crawler.LocalPort, Net.KrpcQuery("get_peers",
            new BEncodedDictionary { { "id", Id(1) }, { "info_hash", new BEncodedString(Net.Bytes(5, 1)) } }));

        Assert.Empty(_observed);
        Assert.Equal("r", ((BEncodedString)reply["y"]).Text); // still answered like a normal node
    }

    [Fact]
    public async Task Find_node_returns_nodes_we_have_learned_from_other_peers()
    {
        using var alice = NewClient();
        using var bob = NewClient();
        var alicePort = ((IPEndPoint)alice.Client.LocalEndPoint!).Port;

        // Alice introduces herself, so she enters the routing table.
        await Net.RoundTripAsync(alice, _crawler.LocalPort,
            Net.KrpcQuery("ping", new BEncodedDictionary { { "id", Id(0xA1) } }));

        var reply = await Net.RoundTripAsync(bob, _crawler.LocalPort, Net.KrpcQuery("find_node",
            new BEncodedDictionary { { "id", Id(0xB0) }, { "target", Id(0xA1) } }));

        var nodes = ((BEncodedString)((BEncodedDictionary)reply["r"])["nodes"]).Span.ToArray();
        Assert.Equal(26 * 2, nodes.Length); // Alice and Bob himself (he is known by now too)

        // Compact node info: 20-byte id, 4-byte IPv4, 2-byte big-endian port. Alice's entry is first (closest to target).
        Assert.Equal(Net.Bytes(20, 0xA1), nodes[..20]);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, nodes[20..24]);
        Assert.Equal(alicePort, (nodes[24] << 8) | nodes[25]);
        Assert.Equal(2, _crawler.NodeCount);
    }

    [Fact]
    public async Task Stopping_releases_the_port_and_marks_the_crawler_stopped()
    {
        var port = _crawler.LocalPort;

        await _crawler.StopAsync();

        Assert.False(_crawler.IsRunning);
        Assert.Equal(0, _crawler.LocalPort);
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Any, port)); // would throw if still held
    }

    [Fact]
    public async Task A_port_that_is_already_in_use_fails_cleanly()
    {
        await using var second = new DhtCrawler(Array.Empty<(string, int)>());

        await Assert.ThrowsAsync<SocketException>(() => second.StartAsync(_crawler.LocalPort, CancellationToken.None));

        Assert.False(second.IsRunning);
        Assert.Equal(0, second.LocalPort);
    }
}
