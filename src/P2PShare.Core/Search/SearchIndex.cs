using System.Text.Json;
using Microsoft.Data.Sqlite;
using P2PShare.Core.Engine;

namespace P2PShare.Core.Search;

/// <summary>
/// Local, per-user search index. Nothing here is a central server: every row is a torrent the
/// DHT crawler observed circulating on the public network and whose name/file-list we then
/// confirmed by fetching its real metadata over the BitTorrent wire protocol. Search queries never
/// leave this machine - they just run against this SQLite database.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _ftsAvailable;

    public SearchIndex(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection($"Data Source={databasePath}");
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
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS torrents_fts USING fts5(name, content='torrents', content_rowid='id');

                CREATE TRIGGER IF NOT EXISTS torrents_ai AFTER INSERT ON torrents BEGIN
                  INSERT INTO torrents_fts(rowid, name) VALUES (new.id, new.name);
                END;
                CREATE TRIGGER IF NOT EXISTS torrents_ad AFTER DELETE ON torrents BEGIN
                  INSERT INTO torrents_fts(torrents_fts, rowid, name) VALUES('delete', old.id, old.name);
                END;
                CREATE TRIGGER IF NOT EXISTS torrents_au AFTER UPDATE ON torrents BEGIN
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

    public void UpsertResolved(FetchedMetadata metadata)
    {
        var filesJson = JsonSerializer.Serialize(metadata.Files.Select(f => new IndexedFile(f.Path, f.Length)));
        var magnet = BuildMagnet(metadata.InfoHashHex, metadata.Name);
        var nowIso = DateTime.UtcNow.ToString("o");

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
        cmd.Parameters.AddWithValue("$hash", metadata.InfoHashHex.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$name", metadata.Name);
        cmd.Parameters.AddWithValue("$size", metadata.TotalSize);
        cmd.Parameters.AddWithValue("$count", metadata.Files.Count);
        cmd.Parameters.AddWithValue("$files", filesJson);
        cmd.Parameters.AddWithValue("$magnet", magnet);
        cmd.Parameters.AddWithValue("$now", nowIso);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Query("SELECT info_hash, name, total_size, file_count, first_seen_utc, last_seen_utc, seen_count, magnet FROM torrents ORDER BY last_seen_utc DESC LIMIT $limit",
                cmd => cmd.Parameters.AddWithValue("$limit", limit));

        if (_ftsAvailable)
        {
            var ftsQuery = BuildFtsQuery(query);
            return Query("""
                SELECT t.info_hash, t.name, t.total_size, t.file_count, t.first_seen_utc, t.last_seen_utc, t.seen_count, t.magnet
                FROM torrents_fts f JOIN torrents t ON t.id = f.rowid
                WHERE torrents_fts MATCH $query
                ORDER BY t.seen_count DESC
                LIMIT $limit
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$query", ftsQuery);
                    cmd.Parameters.AddWithValue("$limit", limit);
                });
        }

        return Query("""
            SELECT info_hash, name, total_size, file_count, first_seen_utc, last_seen_utc, seen_count, magnet
            FROM torrents WHERE name LIKE $pattern ORDER BY seen_count DESC LIMIT $limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$pattern", $"%{query}%");
                cmd.Parameters.AddWithValue("$limit", limit);
            });
    }

    private static string BuildFtsQuery(string userQuery)
    {
        var terms = userQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var escaped = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*");
        return string.Join(" ", escaped);
    }

    private List<SearchResult> Query(string sql, Action<SqliteCommand> bind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var results = new List<SearchResult>();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
                DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
                reader.GetInt32(6),
                reader.GetString(7)));
        }
        return results;
    }

    public int TotalIndexed()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM torrents";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static string BuildMagnet(string infoHashHex, string name) =>
        $"magnet:?xt=urn:btih:{infoHashHex}&dn={Uri.EscapeDataString(name)}";

    public void Dispose() => _connection.Dispose();
}
