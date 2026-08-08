using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Photon.Models;

namespace Photon.Core;
public record AlbumRecord(long Id, string Name, string Description, string CoverPath, int ItemCount);

/// <summary>
/// SQLite-backed persistence for favorites and (eventually) the library
/// index. The schema is intentionally simple — one table per concept,
/// versioned via the user_version PRAGMA so future migrations can be
/// chained. Lives at <see cref="AppPaths.LibraryDbPath"/>.
/// </summary>
public sealed class LibraryDatabase
{
    private readonly ILogger<LibraryDatabase> _log;
    private readonly object _writeLock = new();

    public LibraryDatabase(ILogger<LibraryDatabase> log)
    {
        _log = log;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS favorites (
                        path TEXT PRIMARY KEY NOT NULL,
                        added_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS albums (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        description TEXT,
                        cover_path TEXT,
                        created_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS album_items (
                        album_id INTEGER NOT NULL,
                        path TEXT NOT NULL,
                        PRIMARY KEY (album_id, path),
                        FOREIGN KEY (album_id) REFERENCES albums(id) ON DELETE CASCADE
                    );
                    PRAGMA user_version = 1;
                    """;
                cmd.ExecuteNonQuery();
            }

            // Safe migration for existing databases
            try {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE albums ADD COLUMN description TEXT;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "ALTER TABLE albums ADD COLUMN cover_path TEXT;";
                cmd.ExecuteNonQuery();
            } catch { /* Columns already exist */ }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to initialize library database");
        }
    }

    public HashSet<string> GetAlbumItemPaths(long albumId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM album_items WHERE album_id = $a";
            cmd.Parameters.AddWithValue("$a", albumId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        catch (Exception ex) { _log.LogWarning(ex, "GetAlbumItemPaths failed"); }
        return set;
    }

    public void RemoveFromAlbum(long albumId, string path)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM album_items WHERE album_id = $a AND path = $p";
            cmd.Parameters.AddWithValue("$a", albumId);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }
    
    // Replace the old ListAlbums with this one:
    public List<AlbumRecord> ListAlbums()
    {
        var list = new List<AlbumRecord>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Count items per album dynamically
            cmd.CommandText = @"
                SELECT a.id, a.name, a.description, a.cover_path, 
                       (SELECT COUNT(*) FROM album_items ai WHERE ai.album_id = a.id) as item_count 
                FROM albums a 
                ORDER BY a.name";
            
            using var r = cmd.ExecuteReader();
            while (r.Read()) 
            {
                list.Add(new AlbumRecord(
                    Id: r.GetInt64(0),
                    Name: r.GetString(1),
                    Description: r.IsDBNull(2) ? "" : r.GetString(2),
                    CoverPath: r.IsDBNull(3) ? "" : r.GetString(3),
                    ItemCount: r.GetInt32(4)
                ));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "ListAlbums failed"); }
        return list;
    }

    // Add these two new methods for editing:
    public void UpdateAlbum(long id, string name, string description, string coverPath)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE albums SET name = $n, description = $d, cover_path = $c WHERE id = $i";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", description ?? "");
            cmd.Parameters.AddWithValue("$c", coverPath ?? "");
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAlbum(long id)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Due to ON DELETE CASCADE, album_items will clean themselves up
            cmd.CommandText = "DELETE FROM albums WHERE id = $i";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    private static SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={AppPaths.LibraryDbPath}");
        conn.Open();
        return conn;
    }

    // ----- Favorites -----

    public bool IsFavorite(string path)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM favorites WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }

    public void SetFavorite(string path, bool favorite)
    {
        lock (_writeLock)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                if (favorite)
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO favorites (path, added_at) VALUES ($p, $t)";
                    cmd.Parameters.AddWithValue("$p", path);
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                }
                else
                {
                    cmd.CommandText = "DELETE FROM favorites WHERE path = $p";
                    cmd.Parameters.AddWithValue("$p", path);
                }
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SetFavorite failed for {Path}", path);
            }
        }
    }

    public HashSet<string> LoadAllFavoritePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM favorites";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LoadAllFavoritePaths failed");
        }
        return set;
    }

    // ----- Albums -----

    public long CreateAlbum(string name)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO albums (name, created_at) VALUES ($n, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void AddToAlbum(long albumId, string path)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO album_items (album_id, path) VALUES ($a, $p)";
            cmd.Parameters.AddWithValue("$a", albumId);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }
}
