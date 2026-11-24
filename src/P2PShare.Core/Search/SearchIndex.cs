using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using P2PShare.Core.Engine;

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

    private const string SelectColumns =
        "info_hash, name, total_size, file_count, first_seen_utc, last_seen_utc, seen_count, magnet";

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
                    seen_count INTEGER NOT NULL DEFAULT 1
                );
                """;
            cmd.ExecuteNonQuery();
        }

        _ftsAvailable = TryEnableFullTextSearch();
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

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO torrents (info_hash, name, total_size, file_count, files_json, magnet, first_seen_utc, last_seen_utc, seen_count)
                VALUES ($hash, $name, $size, $count, $files, $magnet, $now, $now, 1)
                ON CONFLICT(info_hash) DO UPDATE SET
                    name = excluded.name,
                    total_size = excluded.total_size,
                    file_count = excluded.file_count,
                    files_json = excluded.files_json,
                    last_seen_utc = excluded.last_seen_utc,
                    seen_count = seen_count + 1;
                """;
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

    public IReadOnlyList<SearchResult> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Query($"SELECT {SelectColumns} FROM torrents ORDER BY last_seen_utc DESC LIMIT $limit",
                cmd => cmd.Parameters.AddWithValue("$limit", limit));

        if (_ftsAvailable)
        {
            var ftsQuery = BuildFtsQuery(query);
            return Query("""
                SELECT t.info_hash, t.name, t.total_size, t.file_count, t.first_seen_utc, t.last_seen_utc, t.seen_count, t.magnet
                FROM torrents_fts f JOIN torrents t ON t.id = f.rowid
                WHERE torrents_fts MATCH $query
                ORDER BY t.seen_count DESC, t.last_seen_utc DESC
                LIMIT $limit
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$query", ftsQuery);
                    cmd.Parameters.AddWithValue("$limit", limit);
                });
        }

        return Query($"""
            SELECT {SelectColumns}
            FROM torrents WHERE name LIKE $pattern ESCAPE '\' ORDER BY seen_count DESC, last_seen_utc DESC LIMIT $limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
                cmd.Parameters.AddWithValue("$limit", limit);
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
        cmd.CommandText = $"SELECT {SelectColumns} FROM torrents WHERE info_hash = $hash";
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
        reader.GetString(7));

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
