using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent.BEncoding;
using P2PShare.Core.Dht;

namespace P2PShare.Core.Tests;

/// <summary>
/// BEP 51 sampling, driven over loopback by a fake DHT node that plays the remote side: it
/// introduces itself to the crawler, waits for the crawler's <c>sample_infohashes</c> question and
/// answers it (or doesn't) in various ways. No bootstrap hosts, so nothing touches the internet.
/// </summary>
public class DhtSamplingTests : IAsyncLifetime
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    // A short housekeeping interval so the crawler asks its questions within a fraction of a second.
    private readonly DhtCrawler _crawler = new(Array.Empty<(string, int)>(), TimeSpan.FromMilliseconds(100));
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

    private IPEndPoint CrawlerEndPoint => new(IPAddress.Loopback, _crawler.LocalPort);

    private UdpClient NewClient()
    {
        var client = Net.NewLoopbackClient();
        _clients.Add(client);
        return client;
    }

    /// <summary>Pings the crawler, which is how a node becomes known to it.</summary>
    private async Task<UdpClient> NewKnownNodeAsync()
    {
        var node = NewClient();
        await Net.RoundTripAsync(node, _crawler.LocalPort,
            Net.KrpcQuery("ping", new BEncodedDictionary { { "id", new BEncodedString(Net.Bytes(20, 7)) } }));
        return node;
    }

    private static async Task<BEncodedDictionary?> NextQueryAsync(UdpClient node, string method, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var received = await node.ReceiveAsync(cts.Token);
                var message = BEncodedValue.Decode<BEncodedDictionary>(received.Buffer);
                if (message.TryGetValue("q", out var q) && q is BEncodedString name && name.Text == method)
                    return message;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private Task ReplyAsync(UdpClient node, BEncodedValue transactionId, BEncodedDictionary r) =>
        node.SendAsync(new BEncodedDictionary
        {
            { "t", transactionId },
            { "y", new BEncodedString("r") },
            { "r", r },
        }.Encode(), CrawlerEndPoint).AsTask();

    private static BEncodedString Hashes(params byte[] fills) =>
        new(fills.SelectMany(f => Net.Bytes(20, f)).ToArray());

    private static BEncodedDictionary SampleReply(BEncodedString samples, long interval = 3600) => new()
    {
        { "id", new BEncodedString(Net.Bytes(20, 7)) },
        { "interval", new BEncodedNumber(interval) },
        { "num", new BEncodedNumber(5000) },
        { "nodes", new BEncodedString("") },
        { "samples", samples },
    };

    private async Task<(UdpClient Node, BEncodedDictionary Question)> AskedNodeAsync()
    {
        var node = await NewKnownNodeAsync();
        var question = await NextQueryAsync(node, "sample_infohashes", TimeSpan.FromSeconds(5));
        Assert.NotNull(question);
        return (node, question!);
    }

    [Fact]
    public async Task A_known_node_is_asked_for_samples_and_the_hashes_are_reported()
    {
        var (node, question) = await AskedNodeAsync();

        var args = (BEncodedDictionary)question["a"];
        Assert.Equal(20, ((BEncodedString)args["id"]).Span.Length);
        Assert.Equal(20, ((BEncodedString)args["target"]).Span.Length);

        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x11, 0x22, 0x33)));

        Assert.True(await Net.WaitUntilAsync(() => _observed.Count == 3, TimeSpan.FromSeconds(3)));
        Assert.All(_observed, o => Assert.Equal(ObservationKind.Sample, o.Kind));
        Assert.All(_observed, o => Assert.Equal(((IPEndPoint)node.Client.LocalEndPoint!).Port, o.From.Port));
        Assert.Equal(
            new[] { 0x11, 0x22, 0x33 }.Select(f => new NodeId(Net.Bytes(20, (byte)f))).ToHashSet(),
            _observed.Select(o => o.InfoHash).ToHashSet());
        Assert.Equal(3, _crawler.SampledInfoHashes);
        Assert.Equal(1, _crawler.SampleReplies);
        Assert.True(_crawler.SampleQueriesSent >= 1);
    }

    [Fact]
    public async Task Sampling_questions_carry_their_own_transaction_tag_and_ordinary_ones_do_not()
    {
        var node = await NewKnownNodeAsync();

        var sample = await NextQueryAsync(node, "sample_infohashes", TimeSpan.FromSeconds(5));
        Assert.NotNull(sample);
        var sampleId = ((BEncodedString)sample!["t"]).Span;
        Assert.Equal(2, sampleId.Length);
        Assert.Equal((byte)'S', sampleId[0]);

        // Ordinary find_node questions never use that first byte, so the two can't be confused.
        for (int i = 0; i < 20; i++)
        {
            var ordinary = await NextQueryAsync(node, "find_node", TimeSpan.FromSeconds(5));
            Assert.NotNull(ordinary);
            Assert.NotEqual((byte)'S', ((BEncodedString)ordinary!["t"]).Span[0]);
        }
    }

    [Fact]
    public async Task A_node_is_not_asked_again_before_its_interval_is_up()
    {
        var (node, question) = await AskedNodeAsync();

        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x11), interval: 3600));

        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet));
    }

    [Fact]
    public async Task An_interval_of_zero_is_not_taken_literally()
    {
        var (node, question) = await AskedNodeAsync();

        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x11), interval: 0));

        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet));
    }

    [Fact]
    public async Task A_reply_without_samples_means_no_support_and_the_node_is_left_alone()
    {
        var (node, question) = await AskedNodeAsync();

        // A node that doesn't know sample_infohashes typically answers like find_node.
        await ReplyAsync(node, question["t"], new BEncodedDictionary
        {
            { "id", new BEncodedString(Net.Bytes(20, 7)) },
            { "nodes", new BEncodedString("") },
        });

        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet));
        Assert.Empty(_observed);
        Assert.Equal(0, _crawler.SampleReplies);
    }

    [Fact]
    public async Task An_empty_but_present_samples_field_still_counts_as_support()
    {
        var (node, question) = await AskedNodeAsync();

        await ReplyAsync(node, question["t"], SampleReply(new BEncodedString("")));

        Assert.True(await Net.WaitUntilAsync(() => _crawler.SampleReplies == 1, TimeSpan.FromSeconds(3)));
        Assert.Empty(_observed);
        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet)); // and it waits out the interval
    }

    [Fact]
    public async Task A_second_reply_to_the_same_question_is_ignored()
    {
        var (node, question) = await AskedNodeAsync();
        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x11)));
        Assert.True(await Net.WaitUntilAsync(() => _observed.Count == 1, TimeSpan.FromSeconds(3)));

        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x44, 0x55)));
        await Task.Delay(400);

        Assert.Single(_observed);
        Assert.Equal(1, _crawler.SampledInfoHashes);
    }

    [Fact]
    public async Task Samples_from_an_address_we_never_asked_are_ignored()
    {
        using var stranger = NewClient();

        await ReplyAsync(stranger, new BEncodedString("S1"), SampleReply(Hashes(0x66, 0x77)));
        await Task.Delay(500);

        Assert.Empty(_observed);
        Assert.Equal(0, _crawler.SampledInfoHashes);
    }

    [Fact]
    public async Task An_oversized_reply_is_capped_and_a_trailing_partial_hash_is_dropped()
    {
        var (node, question) = await AskedNodeAsync();

        // 150 hashes and 7 stray bytes: far more than fits in a packet, and not a whole hash at the end.
        var samples = new BEncodedString(Enumerable.Range(0, 150).SelectMany(i => Net.Bytes(20, (byte)(i + 1))).Concat(Net.Bytes(7, 0xEE)).ToArray());
        await ReplyAsync(node, question["t"], SampleReply(samples));

        Assert.True(await Net.WaitUntilAsync(() => _observed.Count >= 100, TimeSpan.FromSeconds(3)));
        await Task.Delay(200);
        Assert.Equal(100, _observed.Count);
        Assert.DoesNotContain(_observed, o => o.InfoHash.Equals(new NodeId(Net.Bytes(20, 0xEE))));
    }

    [Fact]
    public async Task A_missing_or_odd_interval_falls_back_to_a_sensible_wait()
    {
        var (node, question) = await AskedNodeAsync();
        var reply = SampleReply(Hashes(0x11));
        reply.Remove("interval");

        await ReplyAsync(node, question["t"], reply);

        Assert.True(await Net.WaitUntilAsync(() => _observed.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet));
    }

    [Fact]
    public async Task Garbage_datagrams_ahead_of_a_real_reply_do_not_disturb_sampling()
    {
        var (node, question) = await AskedNodeAsync();

        await node.SendAsync(new byte[] { 1, 2, 3, 4, 5 }, CrawlerEndPoint);
        await node.SendAsync(new BEncodedDictionary { { "y", new BEncodedString("r") } }.Encode(), CrawlerEndPoint); // no "r" at all
        await ReplyAsync(node, question["t"], SampleReply(Hashes(0x11)));

        Assert.True(await Net.WaitUntilAsync(() => _observed.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.True(_crawler.IsRunning);
    }

    [Fact]
    public async Task A_samples_field_of_the_wrong_type_is_treated_as_no_support()
    {
        var (node, question) = await AskedNodeAsync();

        await ReplyAsync(node, question["t"], new BEncodedDictionary
        {
            { "id", new BEncodedString(Net.Bytes(20, 7)) },
            { "samples", new BEncodedNumber(5) },
        });

        Assert.Null(await NextQueryAsync(node, "sample_infohashes", Quiet));
        Assert.Empty(_observed);
        Assert.True(_crawler.IsRunning);
    }
}
