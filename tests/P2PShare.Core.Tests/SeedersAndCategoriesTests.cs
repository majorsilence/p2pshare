using Microsoft.Data.Sqlite;
using P2PShare.Core.Engine;
using P2PShare.Core.Net;
using P2PShare.Core.Search;
using P2PShare.Core.Settings;

namespace P2PShare.Core.Tests;

/// <summary>Seeder counts and categories, from the database up to the service.</summary>
public class SeedersAndCategoriesTests
{
    private static string Hash(int n) => n.ToString("x40");

    private static void Add(SearchIndex index, int n, string name, string? file = null) =>
        index.UpsertResolved(new FetchedMetadata(Hash(n), name, 100, new[] { (file ?? "a.mkv", 100L) }));

    [Fact]
    public void A_new_entry_is_categorised_and_has_no_counts_yet()
    {
        using var dir = new TempDir();
        using var index = new SearchIndex(dir.File("search.db"));

        Add(index, 1, "An Album", "song.mp3");

        var result = index.Get(Hash(1))!;
        Assert.Equal(ContentCategory.Music, result.Category);
        Assert.Null(result.Seeders);
        Assert.Null(result.Leechers);
    }

    [Fact]
    public void Search_can_be_limited_to_a_category_or_leave_adult_results_out()
    {
        using var dir = new TempDir();
        using var index = new SearchIndex(dir.File("search.db"));
        Add(index, 1, "ocean film");
        Add(index, 2, "ocean album", "song.mp3");
        Add(index, 3, "ocean xxx");

        Assert.Equal(3, index.Search("ocean").Count);
        Assert.Equal("ocean album", Assert.Single(index.Search("ocean", category: ContentCategory.Music)).Name);
        Assert.Equal(2, index.Search("ocean", excludeAdult: true).Count);
        Assert.Equal(2, index.Search("", excludeAdult: true).Count);
        // Asking for the adult category explicitly shows it even with "hide adult" on.
        Assert.Equal("ocean xxx", Assert.Single(index.Search("ocean", category: ContentCategory.Adult, excludeAdult: true)).Name);
        Assert.Single(index.Search("", category: ContentCategory.Video));
    }

    [Fact]
    public void Counts_are_stored_and_unknown_hashes_ignored()
    {
        using var dir = new TempDir();
        using var index = new SearchIndex(dir.File("search.db"));
        Add(index, 1, "thing");

        index.RecordScrape(new Dictionary<string, ScrapeCounts>
        {
            [Hash(1).ToUpperInvariant()] = new(5, 20, 3),
            [Hash(99)] = new(1, 1, 1),
        });

        var result = index.Get(Hash(1))!;
        Assert.Equal(5, result.Seeders);
        Assert.Equal(3, result.Leechers);
        Assert.Equal(1, index.TotalIndexed());
    }

    [Fact]
    public void Results_rank_seeded_first_then_unchecked_then_those_with_nobody()
    {
        using var dir = new TempDir();
        using var index = new SearchIndex(dir.File("search.db"));
        Add(index, 1, "ocean dead");
        Add(index, 2, "ocean unchecked");
        Add(index, 3, "ocean popular");
        Add(index, 4, "ocean some");
        index.RecordScrape(new Dictionary<string, ScrapeCounts>
        {
            [Hash(1)] = new(0, 0, 0),
            [Hash(3)] = new(500, 0, 0),
            [Hash(4)] = new(2, 0, 0),
        });

        Assert.Equal(
            new[] { "ocean popular", "ocean some", "ocean unchecked", "ocean dead" },
            index.Search("ocean").Select(r => r.Name));
    }

    [Fact]
    public void Never_checked_torrents_are_scraped_first_and_fresh_counts_are_left_alone()
    {
        using var dir = new TempDir();
        using var index = new SearchIndex(dir.File("search.db"));
        Add(index, 1, "one");
        Add(index, 2, "two");
        Add(index, 3, "three");
        index.RecordScrape(new Dictionary<string, ScrapeCounts> { [Hash(2)] = new(1, 0, 0) });

        Assert.Equal(new[] { Hash(3), Hash(1) }, index.HashesNeedingScrape(10, TimeSpan.FromHours(1)));
        Assert.Equal(new[] { Hash(3) }, index.HashesNeedingScrape(1, TimeSpan.FromHours(1)));
        // Once the counts are older than allowed they come round again, after the unchecked ones.
        Assert.Equal(new[] { Hash(3), Hash(1), Hash(2) }, index.HashesNeedingScrape(10, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_database_from_before_categories_and_counts_is_upgraded_in_place()
    {
        using var dir = new TempDir();
        var path = dir.File("search.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE torrents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, info_hash TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                    total_size INTEGER NOT NULL, file_count INTEGER NOT NULL, files_json TEXT NOT NULL, magnet TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, seen_count INTEGER NOT NULL DEFAULT 1);
                INSERT INTO torrents (info_hash, name, total_size, file_count, files_json, magnet, first_seen_utc, last_seen_utc)
                VALUES ('0000000000000000000000000000000000000001', 'old album', 5, 1, '[{"Path":"a.flac","Length":5}]', 'magnet:?x', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                       ('0000000000000000000000000000000000000002', 'old damaged ebook', 5, 1, 'not json', 'magnet:?x', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var index = new SearchIndex(path))
        {
            Assert.Equal(ContentCategory.Music, index.Get(Hash(1))!.Category);
            Assert.Equal(ContentCategory.Books, index.Get(Hash(2))!.Category); // from the name when the file list is unreadable
            Assert.Null(index.Get(Hash(1))!.Seeders);
            index.RecordScrape(new Dictionary<string, ScrapeCounts> { [Hash(1)] = new(2, 0, 1) });
        }

        // Opening it again must not try to add the columns twice or lose anything.
        using var reopened = new SearchIndex(path);
        Assert.Equal(2, reopened.Get(Hash(1))!.Seeders);
    }

    [Fact]
    public void Labels_from_an_older_set_of_rules_are_redone()
    {
        using var dir = new TempDir();
        var path = dir.File("search.db");
        using (var index = new SearchIndex(path)) Add(index, 1, "An Album", "song.mp3");
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE torrents SET category = 'Software'; PRAGMA user_version = 0;";
            cmd.ExecuteNonQuery();
        }

        using var reopened = new SearchIndex(path);

        Assert.Equal(ContentCategory.Music, reopened.Get(Hash(1))!.Category);
    }

    // ---- through the service ----------------------------------------------------------------

    private static AppSettings OfflineSettings(TempDir dir, params string[] trackers)
    {
        var settings = AppSettings.Load(dir.Path);
        settings.DownloadsDirectory = dir.File("downloads");
        settings.ListenPort = Net.FreeTcpPort();
        settings.CrawlerPort = 0;
        settings.Trackers = trackers.ToList();
        return settings;
    }

    [Fact]
    public async Task The_service_fills_in_counts_from_the_configured_trackers()
    {
        using var dir = new TempDir();
        await using var tracker = new FakeUdpTracker();
        tracker.Known[Hash(1)] = new ScrapeCounts(42, 100, 7);
        await using var service = new P2PShareService(OfflineSettings(dir, tracker.Url), enableNetwork: false);
        Add(service.Index, 1, "ocean one");
        Add(service.Index, 2, "ocean two");

        Assert.Equal(2, await service.ScrapeOnceAsync(CancellationToken.None));

        var results = service.Index.Search("ocean");
        Assert.Equal(42, results[0].Seeders);
        Assert.Equal(7, results[0].Leechers);
        Assert.Equal(0, results[1].Seeders);
        // Nothing is stale yet, so the next round asks nobody.
        int asked = tracker.Received;
        Assert.Equal(0, await service.ScrapeOnceAsync(CancellationToken.None));
        Assert.Equal(asked, tracker.Received);
    }

    [Fact]
    public async Task Searching_never_sends_anything_to_a_tracker()
    {
        using var dir = new TempDir();
        await using var tracker = new FakeUdpTracker();
        await using var service = new P2PShareService(OfflineSettings(dir, tracker.Url), enableNetwork: false);
        await service.StartAsync();
        Add(service.Index, 1, "ocean");

        service.Index.Search("ocean");
        service.Index.Search("");
        await Task.Delay(300);

        Assert.Equal(0, tracker.Received);
    }

    [Fact]
    public async Task Turning_seeder_counts_off_stops_the_scraping()
    {
        using var dir = new TempDir();
        await using var tracker = new FakeUdpTracker();
        var settings = OfflineSettings(dir, tracker.Url);
        settings.ScrapeSeederCounts = false;
        await using var service = new P2PShareService(settings, enableNetwork: false);
        Add(service.Index, 1, "ocean");

        Assert.Equal(0, await service.ScrapeOnceAsync(CancellationToken.None));
        Assert.Equal(0, tracker.Received);
        Assert.Null(service.Index.Get(Hash(1))!.Seeders);
    }

    [Fact]
    public async Task When_no_tracker_answers_the_counts_stay_unknown()
    {
        using var dir = new TempDir();
        await using var tracker = new FakeUdpTracker { ErrorOnScrape = true };
        await using var service = new P2PShareService(OfflineSettings(dir, tracker.Url), enableNetwork: false);
        Add(service.Index, 1, "ocean");

        Assert.Null(await service.ScrapeOnceAsync(CancellationToken.None));
        Assert.Null(service.Index.Get(Hash(1))!.Seeders);
    }

    [Fact]
    public void The_setting_is_on_by_default_and_persists()
    {
        using var dir = new TempDir();
        var settings = AppSettings.Load(dir.Path);
        Assert.True(settings.ScrapeSeederCounts);

        settings.ScrapeSeederCounts = false;
        settings.Save();

        Assert.False(AppSettings.Load(dir.Path).ScrapeSeederCounts);
    }
}
