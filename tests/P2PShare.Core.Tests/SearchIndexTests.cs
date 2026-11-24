using Microsoft.Data.Sqlite;
using P2PShare.Core.Engine;
using P2PShare.Core.Search;

namespace P2PShare.Core.Tests;

public class SearchIndexTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SearchIndex _index;

    public SearchIndexTests() => _index = new SearchIndex(_dir.File("search.db"));

    public void Dispose()
    {
        _index.Dispose();
        _dir.Dispose();
    }

    private static string Hash(int n) => n.ToString("x").PadLeft(40, '0');

    private static FetchedMetadata Meta(int n, string name, long size = 1000, int files = 1) =>
        new(Hash(n), name, size, Enumerable.Range(0, files).Select(i => ($"{name}/file{i}.bin", size / Math.Max(files, 1))).ToList());

    [Fact]
    public void Upserted_torrent_is_found_by_a_word_prefix()
    {
        _index.UpsertResolved(Meta(1, "Ubuntu 24.04 Desktop amd64.iso", size: 6_000_000_000));

        Assert.Single(_index.Search("ubu"));
        Assert.Single(_index.Search("desktop"));
        Assert.Single(_index.Search("24.04"));
        Assert.Single(_index.Search("ubuntu amd64"));
        Assert.Empty(_index.Search("fedora"));
    }

    [Fact]
    public void Upsert_returns_the_stored_row()
    {
        var result = _index.UpsertResolved(Meta(2, "Some Album", size: 123, files: 3));

        Assert.Equal(Hash(2), result.InfoHashHex);
        Assert.Equal("Some Album", result.Name);
        Assert.Equal(123, result.TotalSize);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(1, result.SeenCount);
        Assert.StartsWith($"magnet:?xt=urn:btih:{Hash(2)}&dn=Some%20Album", result.Magnet);
    }

    [Fact]
    public void Upserting_again_counts_a_sighting_and_keeps_first_seen()
    {
        var first = _index.UpsertResolved(Meta(3, "Thing"));
        var second = _index.UpsertResolved(Meta(3, "Thing"));

        Assert.Equal(2, second.SeenCount);
        Assert.Equal(first.FirstSeenUtc, second.FirstSeenUtc);
        Assert.Equal(1, _index.TotalIndexed());
    }

    [Fact]
    public void Hashes_are_stored_lower_case_and_lookups_ignore_case()
    {
        var upper = new FetchedMetadata("ABCDEF0123456789ABCDEF0123456789ABCDEF01", "Case Test", 1, new[] { ("a", 1L) });
        _index.UpsertResolved(upper);

        Assert.True(_index.Contains("abcdef0123456789abcdef0123456789abcdef01"));
        Assert.True(_index.Contains("ABCDEF0123456789ABCDEF0123456789ABCDEF01"));
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", _index.Get("ABCDEF0123456789ABCDEF0123456789ABCDEF01")!.InfoHashHex);
    }

    [Fact]
    public void Renaming_a_torrent_updates_the_full_text_index()
    {
        _index.UpsertResolved(Meta(4, "Old Name"));
        _index.UpsertResolved(Meta(4, "Brand New Title"));

        Assert.Empty(_index.Search("old"));
        Assert.Single(_index.Search("brand"));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("*")]
    [InlineData("-")]
    [InlineData("NEAR(")]
    [InlineData("foo AND OR NOT")]
    [InlineData("(((")]
    [InlineData("name:secret")]
    [InlineData("'; DROP TABLE torrents; --")]
    public void Hostile_query_text_cannot_break_the_search(string query)
    {
        _index.UpsertResolved(Meta(5, "Normal Torrent"));

        var ex = Record.Exception(() => _index.Search(query));

        Assert.Null(ex);
        Assert.Equal(1, _index.TotalIndexed());
    }

    [Fact]
    public void Blank_query_lists_everything_most_recently_seen_first()
    {
        _index.UpsertResolved(Meta(6, "first"));
        Thread.Sleep(20);
        _index.UpsertResolved(Meta(7, "second"));
        Thread.Sleep(20);
        _index.UpsertResolved(Meta(8, "third"));

        var names = _index.Search("").Select(r => r.Name).ToArray();

        Assert.Equal(new[] { "third", "second", "first" }, names);
    }

    [Fact]
    public void Limit_is_honoured()
    {
        for (int i = 0; i < 20; i++) _index.UpsertResolved(Meta(100 + i, $"linux build {i}"));

        Assert.Equal(5, _index.Search("linux", 5).Count);
        Assert.Equal(7, _index.Search("", 7).Count);
    }

    [Fact]
    public void Sightings_raise_a_torrents_rank_and_unknown_hashes_are_ignored()
    {
        _index.UpsertResolved(Meta(10, "linux alpha"));
        _index.UpsertResolved(Meta(11, "linux beta"));

        _index.RecordSightings(new Dictionary<string, int>
        {
            [Hash(11).ToUpperInvariant()] = 5,
            [Hash(999)] = 3, // never indexed: must not create a row
        });

        var results = _index.Search("linux");
        Assert.Equal(new[] { "linux beta", "linux alpha" }, results.Select(r => r.Name).ToArray());
        Assert.Equal(6, results[0].SeenCount);
        Assert.Equal(2, _index.TotalIndexed());
        Assert.False(_index.Contains(Hash(999)));
    }

    [Fact]
    public void Sightings_do_not_disturb_the_full_text_index()
    {
        _index.UpsertResolved(Meta(12, "findable thing"));
        _index.RecordSightings(new Dictionary<string, int> { [Hash(12)] = 1 });

        Assert.Single(_index.Search("findable"));
    }

    [Fact]
    public void Empty_sighting_batch_is_a_no_op()
    {
        _index.RecordSightings(new Dictionary<string, int>());
        Assert.Equal(0, _index.TotalIndexed());
    }

    [Fact]
    public void AllInfoHashes_lists_what_is_indexed()
    {
        _index.UpsertResolved(Meta(20, "a"));
        _index.UpsertResolved(Meta(21, "b"));

        Assert.Equal(new[] { Hash(20), Hash(21) }, _index.AllInfoHashes().OrderBy(h => h).ToArray());
    }

    [Fact]
    public void Get_returns_null_for_unknown_hashes()
    {
        Assert.Null(_index.Get(Hash(404)));
    }

    [Fact]
    public void Huge_file_lists_are_capped_but_the_real_count_is_kept()
    {
        var result = _index.UpsertResolved(Meta(30, "big", size: 5000, files: 5000));
        Assert.Equal(5000, result.FileCount);
    }

    [Fact]
    public void Hostile_names_are_cleaned_before_storage()
    {
        var evil = "  Movie‮txt.exe\u0000\r\n\tNOW  " + new string('x', 1000);

        var result = _index.UpsertResolved(Meta(31, evil));

        Assert.DoesNotContain('‮', result.Name);
        Assert.DoesNotContain('\0', result.Name);
        Assert.DoesNotContain('\n', result.Name);
        Assert.StartsWith("Movietxt.exe NOW xxx", result.Name);
        Assert.True(result.Name.Length <= SearchIndex.MaxNameLength);
        Assert.DoesNotContain("‮", result.Magnet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002")]
    public void Names_with_nothing_printable_fall_back_to_the_hash(string? name)
    {
        Assert.Equal("fallback", SearchIndex.SanitizeName(name, "fallback"));
    }

    [Fact]
    public void Plain_names_are_untouched_and_whitespace_is_collapsed()
    {
        Assert.Equal("Plain Name (2024) [1080p]", SearchIndex.SanitizeName("Plain Name (2024) [1080p]", "x"));
        Assert.Equal("a b c", SearchIndex.SanitizeName("a   b\t\tc", "x"));
        Assert.Equal("日本語 のタイトル", SearchIndex.SanitizeName("日本語 のタイトル", "x"));
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_do_not_corrupt_or_throw()
    {
        // The real app has several resolver threads writing while the UI thread searches.
        const int writers = 6, perWriter = 60;

        var tasks = new List<Task>();
        for (int w = 0; w < writers; w++)
        {
            var offset = 1000 + w * perWriter;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++) _index.UpsertResolved(Meta(offset + i, $"concurrent item {offset + i}"));
            }));
        }
        for (int r = 0; r < 3; r++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    _index.Search("concurrent");
                    _index.TotalIndexed();
                    _index.RecordSightings(new Dictionary<string, int> { [Hash(1000)] = 1 });
                }
            }));
        }

        await Task.WhenAll(tasks); // rethrows the first failure, if any

        Assert.Equal(writers * perWriter, _index.TotalIndexed());
    }

    [Fact]
    public void A_database_from_an_earlier_version_is_upgraded_in_place()
    {
        var path = _dir.File("old.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            // Schema exactly as the first release created it, including the broad update trigger.
            cmd.CommandText = """
                CREATE TABLE torrents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, info_hash TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                    total_size INTEGER NOT NULL, file_count INTEGER NOT NULL, files_json TEXT NOT NULL, magnet TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, seen_count INTEGER NOT NULL DEFAULT 1);
                CREATE VIRTUAL TABLE torrents_fts USING fts5(name, content='torrents', content_rowid='id');
                CREATE TRIGGER torrents_ai AFTER INSERT ON torrents BEGIN
                  INSERT INTO torrents_fts(rowid, name) VALUES (new.id, new.name); END;
                CREATE TRIGGER torrents_ad AFTER DELETE ON torrents BEGIN
                  INSERT INTO torrents_fts(torrents_fts, rowid, name) VALUES('delete', old.id, old.name); END;
                CREATE TRIGGER torrents_au AFTER UPDATE ON torrents BEGIN
                  INSERT INTO torrents_fts(torrents_fts, rowid, name) VALUES('delete', old.id, old.name);
                  INSERT INTO torrents_fts(rowid, name) VALUES (new.id, new.name); END;
                INSERT INTO torrents (info_hash, name, total_size, file_count, files_json, magnet, first_seen_utc, last_seen_utc, seen_count)
                VALUES ('00000000000000000000000000000000000000aa', 'legacy entry', 1, 1, '[]', 'magnet:?xt=urn:btih:aa', '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var upgraded = new SearchIndex(path);

        Assert.Single(upgraded.Search("legacy"));
        upgraded.RecordSightings(new Dictionary<string, int> { ["00000000000000000000000000000000000000aa"] = 2 });
        upgraded.UpsertResolved(Meta(40, "legacy entry renamed"));
        Assert.Equal(2, upgraded.Search("legacy").Count);
        Assert.Equal(3, upgraded.Get("00000000000000000000000000000000000000aa")!.SeenCount);
    }

    [Fact]
    public void Data_survives_reopening_the_database()
    {
        var path = _dir.File("persist.db");
        using (var first = new SearchIndex(path))
            first.UpsertResolved(Meta(50, "remember me"));

        using var second = new SearchIndex(path);
        Assert.Single(second.Search("remember"));
    }
}
