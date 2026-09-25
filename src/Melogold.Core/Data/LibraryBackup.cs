using Melogold.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

/// <summary>
/// «Сохранить копию» (docs/spec/backup-format.md §4): своя схема Windows переводится в формат копии Melogold — те же
/// таблицы, что у ViTune и ViMusic, плюс колонки Melogold и метка <c>MelogoldBackup</c>. Такую копию открывают
/// Melogold на Android, Windows и Apple. В копию не входят настройки, вход в аккаунт, состояние синка и кэш; из текстов
/// — только свои (<c>user</c>, <c>file</c>).
/// </summary>
public static class LibraryBackup
{
    /// <summary><c>user_version</c> копии Windows и Apple (Android пишет версию своей базы Room).</summary>
    public const int FormatUserVersion = 31;

    /// <summary>Имя файла, как у Android: <c>Melogold_backup_ггггММддЧЧммсс</c>.</summary>
    public static string SuggestedName => $"Melogold_backup_{DateTime.Now:yyyyMMddHHmmss}";

    /// <summary>Таблицы копии (§2): имена и колонки с учётом регистра.</summary>
    internal const string Schema = """
        CREATE TABLE Song (id TEXT PRIMARY KEY, title TEXT NOT NULL, artistsText TEXT, durationText TEXT, thumbnailUrl TEXT,
            likedAt INTEGER, totalPlayTimeMs INTEGER NOT NULL DEFAULT 0, blacklisted INTEGER NOT NULL DEFAULT 0, explicit INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE Event (id INTEGER PRIMARY KEY, songId TEXT NOT NULL, timestamp INTEGER NOT NULL, playTime INTEGER NOT NULL,
            syncId TEXT, deviceId TEXT);
        CREATE TABLE Lyrics (songId TEXT PRIMARY KEY, fixed TEXT, synced TEXT, startTime INTEGER, fixedSource TEXT, syncedSource TEXT);
        CREATE TABLE Album (id TEXT PRIMARY KEY, title TEXT, thumbnailUrl TEXT, year TEXT, authorsText TEXT, shareUrl TEXT,
            timestamp INTEGER, bookmarkedAt INTEGER);
        CREATE TABLE Artist (id TEXT PRIMARY KEY, name TEXT, thumbnailUrl TEXT, timestamp INTEGER, bookmarkedAt INTEGER);
        CREATE TABLE SongAlbumMap (songId TEXT NOT NULL, albumId TEXT NOT NULL, position INTEGER, PRIMARY KEY (songId, albumId));
        CREATE TABLE SongArtistMap (songId TEXT NOT NULL, artistId TEXT NOT NULL, PRIMARY KEY (songId, artistId));
        CREATE TABLE Playlist (id INTEGER PRIMARY KEY, name TEXT NOT NULL, browseId TEXT, thumbnail TEXT, syncId TEXT);
        CREATE TABLE SongPlaylistMap (songId TEXT NOT NULL, playlistId INTEGER NOT NULL, position INTEGER NOT NULL, PRIMARY KEY (songId, playlistId));
        CREATE TABLE SearchQuery (id INTEGER PRIMARY KEY, query TEXT NOT NULL);
        CREATE TABLE MelogoldBackup (key TEXT PRIMARY KEY, value TEXT);
        """;

    /// <summary>Источник текста словом API §4.10 → словом копии (как у ViTune и Android).</summary>
    private const string SourceToBackup = "CASE {0} WHEN 'user' THEN 'User' WHEN 'file' THEN 'File' WHEN 'youtube_music' THEN 'YouTubeMusic' WHEN 'lrclib' THEN 'LrcLib' WHEN 'kugou' THEN 'KuGou' ELSE NULL END";

    /// <summary>
    /// Копия базы <paramref name="database"/> в <paramref name="target"/>. Все таблицы читаются в одной транзакции —
    /// снимок цельный, даже если библиотека в это время пишется; файл сначала пишется рядом и потом переименовывается.
    /// </summary>
    public static void Export(LibraryDatabase database, string target, string appVersion) => Export(database.Path, target, appVersion);

    /// <summary>То же по пути к базе своей схемы (в том числе старой копии Windows при импорте).</summary>
    internal static void Export(string sourcePath, string target, string appVersion)
    {
        var temp = target + ".tmp";
        File.Delete(temp);
        try
        {
            using (var copy = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                copy.Open();
                Exec(copy, null, Schema);
                Exec(copy, null, "ATTACH DATABASE $path AS src", ("$path", sourcePath));
                using (var transaction = copy.BeginTransaction())
                {
                    Exec(copy, transaction, $$"""
                        INSERT INTO Song (id, title, artistsText, durationText, thumbnailUrl, likedAt, totalPlayTimeMs, blacklisted, explicit)
                        SELECT t.video_id, t.title, t.artists_text,
                               COALESCE(t.duration_text, CASE WHEN t.duration_ms IS NOT NULL THEN (t.duration_ms / 60000) || ':' || printf('%02d', (t.duration_ms / 1000) % 60) END),
                               t.thumbnail_url, t.liked_at, t.total_play_ms,
                               EXISTS (SELECT 1 FROM src.content_blocks b WHERE b.type = 'track' AND b.key = t.video_id),
                               t.explicit
                        FROM src.tracks t;
                        INSERT INTO Event (songId, timestamp, playTime, syncId, deviceId)
                        SELECT video_id, played_at, play_time_ms, event_id, {{DeviceColumn(copy, transaction)}} FROM src.play_events ORDER BY played_at;
                        INSERT INTO Lyrics (songId, fixed, synced, startTime, fixedSource, syncedSource)
                        SELECT video_id, NULLIF(plain, ''), NULLIF(synced, ''),
                               CASE WHEN COALESCE({{OffsetColumn(copy, transaction)}}, 0) = 0 THEN NULL ELSE -{{OffsetColumn(copy, transaction)}} END,
                               CASE WHEN NULLIF(plain, '') IS NULL THEN NULL ELSE {{string.Format(SourceToBackup, PlainSourceColumn(copy, transaction))}} END,
                               CASE WHEN NULLIF(synced, '') IS NULL THEN NULL ELSE {{string.Format(SourceToBackup, "source")}} END
                        FROM src.lyrics
                        WHERE COALESCE(source, '') IN ('user', 'file') OR COALESCE({{PlainSourceColumn(copy, transaction)}}, '') IN ('user', 'file');
                        INSERT INTO Album (id, title, thumbnailUrl, year, authorsText, timestamp, bookmarkedAt)
                        SELECT browse_id, title, thumbnail_url, year, artists_text, bookmarked_at, bookmarked_at FROM src.albums;
                        INSERT INTO Artist (id, name, thumbnailUrl, timestamp, bookmarkedAt)
                        SELECT browse_id, name, thumbnail_url, bookmarked_at, bookmarked_at FROM src.artists;
                        INSERT OR IGNORE INTO SongAlbumMap (songId, albumId, position)
                        SELECT video_id, album_id, NULL FROM src.tracks WHERE album_id IN (SELECT id FROM Album);
                        INSERT OR IGNORE INTO SongArtistMap (songId, artistId)
                        SELECT t.video_id, json_extract(j.value, '$.id') FROM src.tracks t, json_each(t.artists_json) j
                        WHERE t.artists_json IS NOT NULL AND json_valid(t.artists_json) AND json_extract(j.value, '$.id') IN (SELECT id FROM Artist);
                        INSERT INTO Playlist (id, name, browseId, thumbnail, syncId)
                        SELECT id, name, browse_id, thumbnail_url, sync_id FROM src.playlists;
                        INSERT INTO SongPlaylistMap (songId, playlistId, position)
                        SELECT video_id, playlist_id, position FROM src.playlist_items;
                        INSERT INTO SearchQuery (query) SELECT query FROM src.search_history ORDER BY searched_at;
                        """);
                    foreach (var (key, value) in new[] { ("format", "1"), ("platform", "windows"), ("appVersion", appVersion), ("createdAt", IsoTime.Format(IsoTime.NowMs())) })
                        Exec(copy, transaction, "INSERT INTO MelogoldBackup (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
                    transaction.Commit();
                }
                Exec(copy, null, "DETACH DATABASE src");
                Exec(copy, null, $"PRAGMA user_version = {FormatUserVersion}; PRAGMA journal_mode = DELETE;");
            }
            SqliteConnection.ClearAllPools();
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Колонки, которых нет в старых схемах (копия Windows до v4 или до v2): читаются как NULL.</summary>
    private static string DeviceColumn(SqliteConnection c, SqliteTransaction t) => HasColumn(c, t, "play_events", "device_id") ? "device_id" : "NULL";

    private static string OffsetColumn(SqliteConnection c, SqliteTransaction t) => HasColumn(c, t, "lyrics", "offset_ms") ? "offset_ms" : "NULL";

    private static string PlainSourceColumn(SqliteConnection c, SqliteTransaction t) => HasColumn(c, t, "lyrics", "plain_source") ? "plain_source" : "NULL";

    private static bool HasColumn(SqliteConnection c, SqliteTransaction t, string table, string column)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}', 'src') WHERE name = $c";
        command.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    internal static void Exec(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
