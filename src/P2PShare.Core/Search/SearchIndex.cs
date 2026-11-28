using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using P2PShare.Core.Engine;
using P2PShare.Core.Net;

namespace P2PShare.Core.Search;

/// <summary>
/// Local, per-user search index. Nothing here is a central server: every row is a torrent the
/// DHT crawler observed circulating on the public network and whose name/file-list we then
/// confirmed by fetching its real metadata over the BitTorrent wire protocol. Search queries never
/// leave this machine - they just run against this SQLite database.
///
/// Thread safety: the index is written by several resolver threads while the UI thread searches
/// it, and a <see cref="SqliteConnection"/> is not safe for concurrent use, so every operation
/// takes <c>_gate</c>. Statements are tiny, so contention is negligible.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    /// <summary>Torrent names come from strangers; cap what we are willing to store and show.</summary>
    public const int MaxNameLength = 300;

    /// <summary>Only this many file entries are kept per torrent (the real count is still recorded).</summary>
    public const int MaxStoredFiles = 1000;

    // Column order matters to ReadResult. Every query reads from "torrents t".
    private const string SelectColumns =
        "t.info_hash, t.name, t.total_size, t.file_count, t.first_seen_utc, t.last_seen_utc, t.seen_count, t.magnet, " +
        "t.category, t.seeders, t.leechers";

    // Torrents trackers know seeders for come first (most seeded on top), then those not yet
    // checked, then those trackers know nobody for.
    private const string Ranking =
        "CASE WHEN t.seeders > 0 THEN t.seeders WHEN t.seeders IS NULL THEN 0 ELSE -1 END DESC, " +
        "t.seen_count DESC, t.last_seen_utc DESC";

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly bool _ftsAvailable;

    public SearchIndex(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        // One long-lived connection; pooling would only keep the database file locked after Dispose.
        _connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS torrents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    info_hash TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    total_size INTEGER NOT NULL,
                    file_count INTEGER NOT NULL,
                    files_json TEXT NOT NULL,
                    magnet TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    seen_count INTEGER NOT NULL DEFAULT 1,
                    category TEXT,
                    seeders INTEGER,
                    leechers INTEGER,
                    scraped_utc TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Before any row is updated below: this replaces the first release's over-eager update trigger.
        _ftsAvailable = TryEnableFullTextSearch();

        AddMissingColumns(("category", "TEXT"), ("seeders", "INTEGER"), ("leechers", "INTEGER"), ("scraped_utc", "TEXT"));
        CategoriseUncategorised();
    }

    /// <summary>Brings a database made by an earlier version up to the current columns.</summary>
    private void AddMissingColumns(params (string Name, string Type)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('torrents')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(0));
        }

        foreach (var (name, type) in columns)
        {
            if (existing.Contains(name)) continue;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE torrents ADD COLUMN {name} {type}"; // fixed names from above
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Rows indexed before categories existed, or labelled by an older set of rules, are classified
    /// from what was stored for them.
    /// </summary>
    private void CategoriseUncategorised()
    {
        // user_version records which rules the stored labels came from.
        using (var version = _connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) != ContentClassifier.Version)
            {
                version.CommandText = $"UPDATE torrents SET category = NULL; PRAGMA user_version = {ContentClassifier.Version};";
                version.ExecuteNonQuery();
            }
        }

        var pending = new List<(long Id, string Name, string FilesJson)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, files_json FROM torrents WHERE category IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        if (pending.Count == 0) return;

        using var tx = _connection.BeginTransaction();
        using var update = _connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE torrents SET category = $category WHERE id = $id";
        var categoryParam = update.Parameters.Add("$category", SqliteType.Text);
        var idParam = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, name, filesJson) in pending)
        {
            List<IndexedFile>? files = null;
            try { files = JsonSerializer.Deserialize<List<IndexedFile>>(filesJson); }
            catch (JsonException) { /* classify from the name alone */ }

            idParam.Value = id;
            categoryParam.Value = ContentClassifier
                .Classify(name, (files ?? new()).Select(f => (f.Path, f.Length)))
                .ToString();
            update.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private bool TryEnableFullTextSearch()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            // The update trigger only fires when the name changes: sighting counters are bumped
            // constantly and must not rewrite the full-text entry each time. (Databases created by
            // earlier versions have a broader "torrents_au" trigger, which is dropped here.)
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS torrents_fts USING fts5(name, content='torrents', content_rowid='id');

                CREATE TRIGGER IF NOT EXISTS torrents_ai AFTER INSERT ON torrents BEGIN
                  INSERT INTO torrents_fts(rowid, name) VALUES (new.id, new.name);
                END;
                CREATE TRIGGER IF NOT EXISTS torrents_ad AFTER DELETE ON torrents BEGIN
                  INSERT INTO torrents_fts(torrents_fts, rowid, name) VALUES('delete', old.id, old.name);
                END;
                DROP TRIGGER IF EXISTS torrents_au;
                CREATE TRIGGER IF NOT EXISTS torrents_au_name AFTER UPDATE OF name ON torrents BEGIN
                  INSERT INTO torrents_fts(torrents_fts, rowid, name) VALUES('delete', old.id, old.name);
                  INSERT INTO torrents_fts(rowid, name) VALUES (new.id, new.name);
                END;
                """;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            // FTS5 not compiled into this SQLitePCLRaw provider - fall back to a LIKE scan.
            return false;
        }
    }

    /// <summary>Inserts a torrent whose metadata was just resolved (or refreshes it) and returns the stored row.</summary>
    public SearchResult UpsertResolved(FetchedMetadata metadata)
    {
        var hash = metadata.InfoHashHex.ToLowerInvariant();
        var name = SanitizeName(metadata.Name, fallback: hash);
        var filesJson = JsonSerializer.Serialize(
            metadata.Files.Take(MaxStoredFiles).Select(f => new IndexedFile(f.Path, f.Length)));
        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var category = ContentClassifier.Classify(name, metadata.Files);

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO torrents (info_hash, name, total_size, file_count, files_json, magnet, first_seen_utc, last_seen_utc, seen_count, category)
                VALUES ($hash, $name, $size, $count, $files, $magnet, $now, $now, 1, $category)
                ON CONFLICT(info_hash) DO UPDATE SET
                    name = excluded.name,
                    total_size = excluded.total_size,
                    file_count = excluded.file_count,
                    files_json = excluded.files_json,
                    category = excluded.category,
                    last_seen_utc = excluded.last_seen_utc,
                    seen_count = seen_count + 1;
                """;
            cmd.Parameters.AddWithValue("$category", category.ToString());
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$size", metadata.TotalSize);
            cmd.Parameters.AddWithValue("$count", metadata.Files.Count);
            cmd.Parameters.AddWithValue("$files", filesJson);
            cmd.Parameters.AddWithValue("$magnet", BuildMagnet(hash, name));
            cmd.Parameters.AddWithValue("$now", nowIso);
            cmd.ExecuteNonQuery();

            return GetUnlocked(hash)
                ?? throw new InvalidOperationException("Row missing immediately after upsert.");
        }
    }

    /// <summary>
    /// Adds <c>count</c> to the popularity counter of each already-indexed torrent in
    /// <paramref name="sightings"/> (keyed by info-hash). Hashes that aren't indexed are ignored.
    /// </summary>
    public void RecordSightings(IReadOnlyDictionary<string, int> sightings)
    {
        if (sightings.Count == 0) return;
        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE torrents SET seen_count = seen_count + $n, last_seen_utc = $now WHERE info_hash = $hash";
            var nParam = cmd.Parameters.Add("$n", SqliteType.Integer);
            var hashParam = cmd.Parameters.Add("$hash", SqliteType.Text);
            cmd.Parameters.AddWithValue("$now", nowIso);
            foreach (var (hash, count) in sightings)
            {
                nParam.Value = count;
                hashParam.Value = hash.ToLowerInvariant();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Stores what trackers reported. Hashes that aren't indexed are ignored.</summary>
    public void RecordScrape(IReadOnlyDictionary<string, ScrapeCounts> counts)
    {
        if (counts.Count == 0) return;
        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE torrents SET seeders = $seeders, leechers = $leechers, scraped_utc = $now WHERE info_hash = $hash";
            var seeders = cmd.Parameters.Add("$seeders", SqliteType.Integer);
            var leechers = cmd.Parameters.Add("$leechers", SqliteType.Integer);
            var hash = cmd.Parameters.Add("$hash", SqliteType.Text);
            cmd.Parameters.AddWithValue("$now", nowIso);
            foreach (var (infoHash, c) in counts)
            {
                seeders.Value = c.Seeders;
                leechers.Value = c.Leechers;
                hash.Value = infoHash.ToLowerInvariant();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> torrents whose tracker counts are missing or older than
    /// <paramref name="maxAge"/>: never-checked ones first (newest first), then the stalest.
    /// </summary>
    public List<string> HashesNeedingScrape(int limit, TimeSpan maxAge)
    {
        var cutoff = (DateTime.UtcNow - maxAge).ToString("o", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT info_hash FROM torrents
                WHERE scraped_utc IS NULL OR scraped_utc < $cutoff
                ORDER BY scraped_utc IS NOT NULL, scraped_utc, id DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var hashes = new List<string>();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        }
    }

    public SearchResult? Get(string infoHashHex)
    {
        lock (_gate) return GetUnlocked(infoHashHex.ToLowerInvariant());
    }

    public bool Contains(string infoHashHex)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM torrents WHERE info_hash = $hash LIMIT 1";
            cmd.Parameters.AddWithValue("$hash", infoHashHex.ToLowerInvariant());
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Every indexed info-hash (lower-case hex), used to seed the in-memory "already known" set at startup.</summary>
    public List<string> AllInfoHashes()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT info_hash FROM torrents";
            using var reader = cmd.ExecuteReader();
            var hashes = new List<string>();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        }
    }

    /// <summary>
    /// Matches names against <paramref name="query"/>, optionally restricted to one category or with
    /// adult results left out. An empty query lists the most recently seen torrents.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(
        string query, int limit = 200, ContentCategory? category = null, bool excludeAdult = false)
    {
        // Filter clauses are fixed text; the values are bound as parameters.
        var filter = "";
        if (category is not null) filter += " AND t.category = $category";
        else if (excludeAdult) filter += " AND COALESCE(t.category, '') <> 'Adult'";

        void Bind(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("$limit", limit);
            if (category is not null) cmd.Parameters.AddWithValue("$category", category.Value.ToString());
        }

        if (string.IsNullOrWhiteSpace(query))
            return Query($"SELECT {SelectColumns} FROM torrents t WHERE 1 = 1{filter} ORDER BY t.last_seen_utc DESC LIMIT $limit", Bind);

        if (_ftsAvailable)
        {
            var ftsQuery = BuildFtsQuery(query);
            return Query($"""
                SELECT {SelectColumns}
                FROM torrents_fts f JOIN torrents t ON t.id = f.rowid
                WHERE torrents_fts MATCH $query{filter}
                ORDER BY {Ranking}
                LIMIT $limit
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$query", ftsQuery);
                    Bind(cmd);
                });
        }

        return Query($"""
            SELECT {SelectColumns}
            FROM torrents t WHERE t.name LIKE $pattern ESCAPE '\'{filter} ORDER BY {Ranking} LIMIT $limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
                Bind(cmd);
            });
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Turns free text into an FTS5 query where every word is a quoted prefix match, so user input can't inject FTS syntax.</summary>
    private static string BuildFtsQuery(string userQuery)
    {
        var terms = userQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var escaped = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*");
        return string.Join(" ", escaped);
    }

    private SearchResult? GetUnlocked(string lowerHash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM torrents t WHERE t.info_hash = $hash";
        cmd.Parameters.AddWithValue("$hash", lowerHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadResult(reader) : null;
    }

    private List<SearchResult> Query(string sql, Action<SqliteCommand> bind)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            var results = new List<SearchResult>();
            while (reader.Read()) results.Add(ReadResult(reader));
            return results;
        }
    }

    private static SearchResult ReadResult(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetInt32(3),
        ParseUtc(reader.GetString(4)),
        ParseUtc(reader.GetString(5)),
        reader.GetInt32(6),
        reader.GetString(7),
        !reader.IsDBNull(8) && Enum.TryParse<ContentCategory>(reader.GetString(8), out var category) ? category : ContentCategory.Other,
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10));

    private static DateTime ParseUtc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public int TotalIndexed()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM torrents";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Strips control characters and bidirectional-override marks (a hostile name could otherwise
    /// make a result row render as a different, innocent-looking one), collapses runs of
    /// whitespace and caps the length. Falls back to <paramref name="fallback"/> if nothing is left.
    /// </summary>
    public static string SanitizeName(string? name, string fallback)
    {
        if (string.IsNullOrEmpty(name)) return fallback;

        var sb = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        var lastWasSpace = true; // also trims leading whitespace
        foreach (var ch in name)
        {
            if (sb.Length >= MaxNameLength) break;
            if (IsBidiControl(ch)) continue;

            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.Length == 0 ? fallback : sb.ToString();
    }

    private static bool IsBidiControl(char ch) =>
        ch is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    public static string BuildMagnet(string infoHashHex, string name) =>
        $"magnet:?xt=urn:btih:{infoHashHex}&dn={Uri.EscapeDataString(name)}";

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
