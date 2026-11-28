using P2PShare.Core.Net;

namespace P2PShare.Core.Tests;

/// <summary>Scraping against fake trackers on loopback; no real tracker is contacted.</summary>
public class TrackerScraperTests
{
    // Bytes that need care when percent-encoded: NUL, '%', '&', '+', space, '=', 0xFF.
    private const string Awkward = "002526 2b203dff0102030405060708090a0b0c0d";
    private static readonly string HashA = Awkward.Replace(" ", "");
    private static readonly string HashB = new('b', 40);
    private static readonly string Unknown = new('c', 40);

    private static TrackerScraper NewScraper(int attempts = 2) => new(TimeSpan.FromMilliseconds(600), attempts);

    private static Task<IReadOnlyDictionary<string, ScrapeCounts>> Scrape(TrackerScraper scraper, string[] trackers, params string[] hashes) =>
        scraper.ScrapeAsync(trackers, hashes, CancellationToken.None);

    [Fact]
    public async Task A_udp_tracker_reports_counts_and_zeros_for_torrents_it_does_not_know()
    {
        await using var tracker = new FakeUdpTracker();
        tracker.Known[HashA] = new ScrapeCounts(Seeders: 12, Completed: 340, Leechers: 5);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { tracker.Url }, HashA, Unknown);

        Assert.Equal(new ScrapeCounts(12, 340, 5), result[HashA]);
        Assert.Equal(default, result[Unknown]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Upper_case_hashes_come_back_under_lower_case_keys()
    {
        await using var tracker = new FakeUdpTracker();
        tracker.Known[HashB] = new ScrapeCounts(1, 2, 3);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { tracker.Url }, HashB.ToUpperInvariant());

        Assert.Equal(1, result[HashB].Seeders);
    }

    [Fact]
    public async Task Several_trackers_are_merged_by_the_highest_figure_not_added_up()
    {
        await using var one = new FakeUdpTracker();
        await using var two = new FakeUdpTracker();
        one.Known[HashB] = new ScrapeCounts(10, 5, 1);
        two.Known[HashB] = new ScrapeCounts(7, 90, 4);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { one.Url, two.Url }, HashB);

        Assert.Equal(new ScrapeCounts(10, 90, 4), result[HashB]);
    }

    [Fact]
    public async Task A_dead_tracker_contributes_nothing_and_does_not_spoil_the_others()
    {
        await using var dead = new FakeUdpTracker { Silent = true };
        await using var alive = new FakeUdpTracker();
        alive.Known[HashB] = new ScrapeCounts(3, 3, 3);
        using var scraper = NewScraper(attempts: 1);

        var result = await Scrape(scraper, new[] { dead.Url, alive.Url, "udp://no-such-host.invalid:80", "wss://x", "garbage" }, HashB);

        Assert.Equal(3, result[HashB].Seeders);
    }

    [Fact]
    public async Task No_answer_at_all_is_different_from_zero_peers()
    {
        await using var dead = new FakeUdpTracker { Silent = true };
        using var scraper = NewScraper(attempts: 1);

        Assert.Empty(await Scrape(scraper, new[] { dead.Url }, HashB));
    }

    [Fact]
    public async Task A_lost_datagram_is_sent_again()
    {
        await using var tracker = new FakeUdpTracker { IgnoreFirstDatagrams = 1 };
        tracker.Known[HashB] = new ScrapeCounts(8, 0, 0);
        using var scraper = NewScraper(attempts: 2);

        var result = await Scrape(scraper, new[] { tracker.Url }, HashB);

        Assert.Equal(8, result[HashB].Seeders);
        Assert.Equal(3, tracker.Received); // connect (lost), connect, scrape
    }

    [Fact]
    public async Task A_reply_with_the_wrong_transaction_id_is_ignored()
    {
        await using var tracker = new FakeUdpTracker { SendStrayReplyFirst = true };
        tracker.Known[HashB] = new ScrapeCounts(4, 0, 0);
        using var scraper = NewScraper();

        Assert.Equal(4, (await Scrape(scraper, new[] { tracker.Url }, HashB))[HashB].Seeders);
    }

    [Fact]
    public async Task An_error_reply_yields_nothing()
    {
        await using var tracker = new FakeUdpTracker { ErrorOnScrape = true };
        using var scraper = NewScraper();

        Assert.Empty(await Scrape(scraper, new[] { tracker.Url }, HashB));
    }

    [Fact]
    public async Task Large_batches_are_split_to_fit_in_a_packet()
    {
        await using var tracker = new FakeUdpTracker();
        var hashes = Enumerable.Range(1, 150).Select(i => i.ToString("x40")).ToArray();
        tracker.Known[hashes[149]] = new ScrapeCounts(99, 0, 0);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { tracker.Url }, hashes);

        Assert.Equal(new[] { 70, 70, 10 }, tracker.ScrapeBatchSizes);
        Assert.Equal(150, result.Count);
        Assert.Equal(99, result[hashes[149]].Seeders); // counts stay matched to the right torrent across batches
        Assert.Equal(0, result[hashes[0]].Seeders);
    }

    [Fact]
    public async Task Nonsensical_counts_are_clamped()
    {
        await using var tracker = new FakeUdpTracker();
        tracker.Known[HashB] = new ScrapeCounts(-5, int.MaxValue, 7);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { tracker.Url }, HashB);

        Assert.Equal(new ScrapeCounts(0, 10_000_000, 7), result[HashB]);
    }

    [Fact]
    public async Task Nothing_to_ask_means_no_traffic()
    {
        await using var tracker = new FakeUdpTracker();
        using var scraper = NewScraper();

        Assert.Empty(await Scrape(scraper, new[] { tracker.Url }));
        Assert.Empty(await Scrape(scraper, Array.Empty<string>(), HashB));
        Assert.Equal(0, tracker.Received);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        await using var dead = new FakeUdpTracker { Silent = true };
        using var scraper = new TrackerScraper(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(200);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scraper.ScrapeAsync(new[] { dead.Url }, new[] { HashB }, cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("http://t.example/announce", "http://t.example/scrape")]
    [InlineData("https://t.example:443/x/announce", "https://t.example:443/x/scrape")]
    [InlineData("http://t.example/announce.php?key=abc", "http://t.example/scrape.php?key=abc")]
    [InlineData("http://t.example/a", null)]
    [InlineData("http://t.example/announce/x", null)]
    public void The_scrape_address_follows_the_announce_address(string announce, string? expected) =>
        Assert.Equal(expected, TrackerScraper.ScrapeUrlFor(announce));

    [Fact]
    public async Task An_http_tracker_is_asked_with_the_raw_hash_bytes_and_its_reply_read()
    {
        await using var tracker = new FakeHttpTracker();
        tracker.Known[HashA] = new ScrapeCounts(6, 70, 2);
        using var scraper = NewScraper();

        var result = await Scrape(scraper, new[] { tracker.Url }, HashA, Unknown);

        Assert.Equal(new ScrapeCounts(6, 70, 2), result[HashA]);
        Assert.Equal(default, result[Unknown]); // left out of a successful reply: the tracker has nobody
        var request = Assert.Single(tracker.Requests);
        Assert.StartsWith("/scrape?info_hash=%00%25%26%2b%20%3d%ff", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_http_failure_reply_yields_nothing()
    {
        await using var tracker = new FakeHttpTracker { Fail = true };
        using var scraper = NewScraper();

        Assert.Empty(await Scrape(scraper, new[] { tracker.Url }, HashB));
    }
}
