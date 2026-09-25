using Melogold.Core.Domain;
using Melogold.Core.Music;
using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

/// <summary>Плейлист, каким его знал сервер после прошлой синхронизации: треки в порядке сервера.</summary>
public sealed record SyncedPlaylist(string SyncId, string? Name, string? ThumbnailUrl, IReadOnlyList<string> VideoIds);

/// <summary>Свой плейлист для синхронизации.</summary>
public sealed record PlaylistRecord(long Id, string? SyncId, string Name, string? BrowseId, string? ThumbnailUrl);

/// <summary>Лайк этого устройства: трек с метаданными и время лайка.</summary>
public sealed record LikeRecord(Track Track, long LikedAt);

/// <summary>Прослушивание этого устройства, ещё не отправленное на сервер.</summary>
public sealed record PlayRecord(string EventId, string VideoId, long PlayedAt, long PlayTimeMs);

/// <summary>«Убрать из истории» (<c>history.forget</c>) или «Очистить историю» (<c>history.clear</c>) для сервера.</summary>
public sealed record HistoryOpRecord(string OpId, string Kind, string? VideoId, long EventsBefore);

/// <summary>Закладка этого устройства: альбом (<c>album</c>) или исполнитель и канал (<c>artist</c>).</summary>
public sealed record BookmarkRecord(string Type, string BrowseId, long BookmarkedAt, string? Title, string? Subtitle, string? ThumbnailUrl, string? Year);

/// <summary>
/// Доступ синхронизации к библиотеке (вариант со снимком, REWRITE §4.12a Android): состояние (<c>binding</c>,
/// <c>cursor</c>, <c>needsMerge</c>, <c>lastSyncAt</c>), снимок сервера (<c>synced_*</c>), <c>sync_id</c> плейлистов
/// и <c>sort_key</c> их треков. Всё внутри одной транзакции <see cref="Run{T}"/>.
/// </summary>
public sealed class SyncStore(Library library)
{
    public Library Library { get; } = library;

    public T Run<T>(Func<SyncTx, T> work) => Library.Database.Write((c, t) => work(new SyncTx(c, t)));

    public void Run(Action<SyncTx> work) => Library.Database.Write((c, t) => work(new SyncTx(c, t)));

    public string? State(string key) => Library.Database.Read(c =>
        LibraryDatabase.Scalar(c, "SELECT value FROM sync_state WHERE key = $k", ("$k", key)) as string);
}

/// <summary>Операции синхронизации в транзакции.</summary>
public sealed class SyncTx
{
    private readonly SqliteConnection _c;
    private readonly SqliteTransaction _t;

    internal SyncTx(SqliteConnection c, SqliteTransaction t)
    {
        _c = c;
        _t = t;
    }

    /// <summary>Что поменялось в библиотеке за транзакцию — для <see cref="Library.Notify"/> после неё.</summary>
    public LibraryChange Changes { get; private set; }

    private int Exec(string sql, params (string Name, object? Value)[] parameters) => LibraryDatabase.Exec(_c, _t, sql, parameters);

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _c.CreateCommand();
        command.Transaction = _t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = _c.CreateCommand();
        command.Transaction = _t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = command.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---------- Состояние ----------

    public string? State(string key) => Scalar("SELECT value FROM sync_state WHERE key = $k", ("$k", key)) as string;

    public void SetState(string key, string? value) =>
        Exec(value is null ? "DELETE FROM sync_state WHERE key = $k" : "INSERT OR REPLACE INTO sync_state (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));

    /// <summary>Другой аккаунт или сервер: снимок, <c>sync_id</c>, <c>sort_key</c> и состояние забываются.</summary>
    public void ForgetBinding()
    {
        Exec("DELETE FROM synced_likes");
        Exec("DELETE FROM synced_playlists");
        Exec("DELETE FROM synced_bookmarks");
        Exec("UPDATE playlists SET sync_id = NULL");
        Exec("UPDATE playlist_items SET sort_key = NULL");
        Exec("DELETE FROM synced_lyrics");
        // История (tasks/0002 §3.6): свои прослушивания примет новый аккаунт, чужие — от прошлого — уходят
        Exec("UPDATE play_events SET synced = 0 WHERE device_id IS NULL");
        Exec("DELETE FROM play_events WHERE device_id IS NOT NULL");
        Exec("DELETE FROM history_ops");
        Exec("DELETE FROM sync_state");
        Changes |= LibraryChange.History;
    }

    // ---------- История (tasks/0002) ----------

    /// <summary>Свои прослушивания, которых сервер ещё не видел, по времени.</summary>
    public List<PlayRecord> UnsentPlays() =>
        Query("SELECT event_id, video_id, played_at, play_time_ms FROM play_events WHERE synced = 0 AND device_id IS NULL ORDER BY played_at",
            r => new PlayRecord(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));

    public void MarkPlaySent(string eventId) => Exec("UPDATE play_events SET synced = 1 WHERE event_id = $e", ("$e", eventId));

    public List<HistoryOpRecord> HistoryOps() =>
        Query("SELECT op_id, kind, video_id, events_before FROM history_ops ORDER BY events_before",
            r => new HistoryOpRecord(r.GetString(0), r.GetString(1), Str(r, 2), r.GetInt64(3)));

    public void DeleteHistoryOp(string opId) => Exec("DELETE FROM history_ops WHERE op_id = $id", ("$id", opId));

    /// <summary>Накопленное время треков (для <c>play.baseline atLeast</c> при первой синхронизации).</summary>
    public List<(string VideoId, long TotalMs)> PlayTotals() =>
        Query("SELECT video_id, total_play_ms FROM tracks WHERE total_play_ms > 0 ORDER BY video_id", r => (r.GetString(0), r.GetInt64(1)));

    /// <summary>Общее время трека с сервера (<c>playStats</c>): уже по всем устройствам.</summary>
    public void SetPlayTotal(string videoId, long totalMs)
    {
        Exec("UPDATE tracks SET total_play_ms = $ms WHERE video_id = $v", ("$ms", totalMs), ("$v", videoId));
        Changes |= LibraryChange.History;
    }

    /// <summary>Прослушивание с сервера: новое вставляется, своё вернувшееся (тот же <c>eventId</c>) не задваивается.</summary>
    public void InsertPlay(string eventId, string videoId, long playedAt, long playTimeMs, string? deviceId)
    {
        if (Exec("INSERT OR IGNORE INTO play_events (event_id, video_id, played_at, play_time_ms, synced, device_id) VALUES ($e, $v, $at, $ms, 1, $d)",
            ("$e", eventId), ("$v", videoId), ("$at", playedAt), ("$ms", playTimeMs), ("$d", deviceId)) > 0)
            Changes |= LibraryChange.History;
    }

    /// <summary><c>playForgets</c>: события трека (или все при <c>*</c>) по <paramref name="eventsBefore"/> включительно удалены.</summary>
    public void ForgetPlays(string videoId, long eventsBefore)
    {
        var removed = videoId == "*"
            ? Exec("DELETE FROM play_events WHERE played_at <= $at", ("$at", eventsBefore))
            : Exec("DELETE FROM play_events WHERE video_id = $v AND played_at <= $at", ("$v", videoId), ("$at", eventsBefore));
        if (removed > 0) Changes |= LibraryChange.History;
    }

    // ---------- Библиотека этого устройства ----------

    public List<LikeRecord> Likes() => Query($"SELECT {Library.TrackColumns} FROM tracks WHERE liked_at IS NOT NULL ORDER BY liked_at",
        r => new LikeRecord(Library.ReadTrack(r), r.GetInt64(12)));

    public List<PlaylistRecord> Playlists() => Query("SELECT id, sync_id, name, browse_id, thumbnail_url FROM playlists ORDER BY created_at, id",
        r => new PlaylistRecord(r.GetInt64(0), Str(r, 1), r.GetString(2), Str(r, 3), Str(r, 4)));

    public PlaylistRecord? PlaylistBySyncId(string syncId) => Query("SELECT id, sync_id, name, browse_id, thumbnail_url FROM playlists WHERE sync_id = $s",
        r => new PlaylistRecord(r.GetInt64(0), Str(r, 1), r.GetString(2), Str(r, 3), Str(r, 4)), ("$s", syncId)).FirstOrDefault();

    public List<string> PlaylistVideoIds(long playlistId) => Library.VideoIds(_c, playlistId);

    public Track? Track(string videoId) => Query($"SELECT {Library.TrackColumns} FROM tracks WHERE video_id = $id", r => Library.ReadTrack(r), ("$id", videoId)).FirstOrDefault();

    public List<BookmarkRecord> Bookmarks()
    {
        var list = Query("SELECT browse_id, bookmarked_at, title, artists_text, thumbnail_url, year FROM albums WHERE bookmarked_at IS NOT NULL",
            r => new BookmarkRecord("album", r.GetString(0), r.GetInt64(1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5)));
        list.AddRange(Query("SELECT browse_id, bookmarked_at, name, thumbnail_url FROM artists WHERE bookmarked_at IS NOT NULL",
            r => new BookmarkRecord("artist", r.GetString(0), r.GetInt64(1), Str(r, 2), null, Str(r, 3), null)));
        return list;
    }

    public void SetPlaylistSyncId(long playlistId, string? syncId) =>
        Exec("UPDATE playlists SET sync_id = $s WHERE id = $id", ("$s", syncId), ("$id", playlistId));

    /// <summary>Треки плейлиста без ключа сервера: уйдут на сервер как новые (копия восстановления).</summary>
    public void ClearSortKeys(long playlistId) => Exec("UPDATE playlist_items SET sort_key = NULL WHERE playlist_id = $id", ("$id", playlistId));

    // ---------- Снимок ----------

    public HashSet<string> SyncedLikes() => Query("SELECT video_id FROM synced_likes", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);

    public Dictionary<string, SyncedPlaylist> SyncedPlaylists() => Query("SELECT sync_id, name, thumbnail_url, video_ids FROM synced_playlists",
        r => new SyncedPlaylist(r.GetString(0), Str(r, 1), Str(r, 2), SplitIds(r.GetString(3)))).ToDictionary(p => p.SyncId, StringComparer.Ordinal);

    public HashSet<(string Type, string BrowseId)> SyncedBookmarks() => Query("SELECT type, browse_id FROM synced_bookmarks", r => (r.GetString(0), r.GetString(1))).ToHashSet();

    public void UpsertSyncedPlaylist(SyncedPlaylist playlist) =>
        Exec("INSERT OR REPLACE INTO synced_playlists (sync_id, name, thumbnail_url, video_ids) VALUES ($s, $n, $t, $v)",
            ("$s", playlist.SyncId), ("$n", playlist.Name), ("$t", playlist.ThumbnailUrl), ("$v", string.Join('\n', playlist.VideoIds)));

    public void DeleteSyncedPlaylist(string syncId) => Exec("DELETE FROM synced_playlists WHERE sync_id = $s", ("$s", syncId));

    private static List<string> SplitIds(string text) => text.Length == 0 ? [] : [.. text.Split('\n')];

    // ---------- Применение ответа сервера (API §4.8) ----------

    /// <summary>
    /// Трек, о котором говорит сервер: новый — с его метаданными, заглушка — с названием <c>videoId</c>. Заглушка прошлой
    /// синхронизации узнаёт настоящее название.
    /// </summary>
    public void EnsureTrack(string videoId, Track? metadata)
    {
        var existing = Scalar("SELECT title FROM tracks WHERE video_id = $id", ("$id", videoId)) as string;
        if (existing is not null)
        {
            if (metadata is not null && existing == videoId && metadata.Title.Length > 0 && metadata.Title != videoId)
            {
                Library.UpsertTrack(_c, _t, metadata);
                Changes |= LibraryChange.Tracks;
            }
            return;
        }
        Library.UpsertTrack(_c, _t, metadata ?? new Track { VideoId = videoId, Title = videoId }, stub: metadata is null);
        Changes |= LibraryChange.Tracks;
    }

    public long InsertPlaylist(string name, string? browseId, string? thumbnailUrl, string syncId, long createdAt)
    {
        Exec("INSERT INTO playlists (sync_id, name, browse_id, thumbnail_url, created_at) VALUES ($s, $n, $b, $t, $at)",
            ("$s", syncId), ("$n", name), ("$b", browseId), ("$t", thumbnailUrl), ("$at", createdAt));
        Changes |= LibraryChange.Playlists;
        return (long)Scalar("SELECT last_insert_rowid()")!;
    }

    public void UpdatePlaylist(long id, string name, string? thumbnailUrl)
    {
        Exec("UPDATE playlists SET name = $n, thumbnail_url = $t WHERE id = $id", ("$n", name), ("$t", thumbnailUrl), ("$id", id));
        Changes |= LibraryChange.Playlists;
    }

    public void DeletePlaylist(long id)
    {
        Exec("DELETE FROM playlist_items WHERE playlist_id = $id", ("$id", id));
        Exec("DELETE FROM playlists WHERE id = $id", ("$id", id));
        Changes |= LibraryChange.Playlists;
    }

    /// <summary>Трек плейлиста с ключом сервера; место выставит <see cref="Reorder"/>.</summary>
    public void UpsertItem(long playlistId, string videoId, string sortKey, long addedAt)
    {
        Exec("""
            INSERT INTO playlist_items (playlist_id, video_id, position, sort_key, added_at) VALUES ($p, $v, 2147483647, $k, $at)
            ON CONFLICT(playlist_id, video_id) DO UPDATE SET sort_key = excluded.sort_key
            """, ("$p", playlistId), ("$v", videoId), ("$k", sortKey), ("$at", addedAt));
        Changes |= LibraryChange.Playlists;
    }

    public void DeleteItem(long playlistId, string videoId)
    {
        Exec("DELETE FROM playlist_items WHERE playlist_id = $p AND video_id = $v", ("$p", playlistId), ("$v", videoId));
        Changes |= LibraryChange.Playlists;
    }

    /// <summary>
    /// Треки с ключом сервера — в его порядке (ключ, затем <c>videoId</c>, побайтово), за ними треки без ключа, как стояли.
    /// В снимок попадают только треки с ключом: добавленное здесь и ещё не отправленное сервер не знает.
    /// </summary>
    public void Reorder(long playlistId)
    {
        var items = Query("SELECT video_id, sort_key, position FROM playlist_items WHERE playlist_id = $id ORDER BY position",
            r => (VideoId: r.GetString(0), SortKey: Str(r, 1), Position: r.GetInt32(2)), ("$id", playlistId));
        var keyed = items.Where(i => i.SortKey is not null)
            .OrderBy(i => i.SortKey, StringComparer.Ordinal).ThenBy(i => i.VideoId, StringComparer.Ordinal).ToList();
        var ordered = keyed.Concat(items.Where(i => i.SortKey is null)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Position != i)
                Exec("UPDATE playlist_items SET position = $pos WHERE playlist_id = $p AND video_id = $v", ("$pos", i), ("$p", playlistId), ("$v", ordered[i].VideoId));
        }
        var playlist = Query("SELECT sync_id, name, thumbnail_url FROM playlists WHERE id = $id", r => (SyncId: Str(r, 0), Name: r.GetString(1), Thumb: Str(r, 2)), ("$id", playlistId)).FirstOrDefault();
        if (playlist.SyncId is null) return;
        UpsertSyncedPlaylist(new SyncedPlaylist(playlist.SyncId, playlist.Name, playlist.Thumb, keyed.Select(i => i.VideoId).ToList()));
    }

    // ---------- Тексты (docs/LYRICS-SYNC.md) ----------

    /// <summary>Свои тексты: хотя бы одна сторона из источника <c>user</c> или <c>file</c>.</summary>
    public Dictionary<string, StoredLyrics> OwnLyrics() => Query($"""
            SELECT video_id, {Library.LyricsColumns} FROM lyrics
            WHERE (source IN ('user', 'file') AND COALESCE(synced, '') <> '') OR (plain_source IN ('user', 'file') AND COALESCE(plain, '') <> '')
            """, r => (Id: r.GetString(0), Lyrics: Library.ReadLyrics(r, 1))).ToDictionary(p => p.Id, p => p.Lyrics, StringComparer.Ordinal);

    public StoredLyrics? Lyrics(string videoId) =>
        Query($"SELECT {Library.LyricsColumns} FROM lyrics WHERE video_id = $v", r => Library.ReadLyrics(r), ("$v", videoId)).FirstOrDefault();

    public void SaveLyrics(string videoId, StoredLyrics lyrics)
    {
        Library.WriteLyrics(_c, _t, videoId, lyrics);
        Changes |= LibraryChange.Lyrics;
    }

    public void DeleteLyrics(string videoId)
    {
        Exec("DELETE FROM lyrics WHERE video_id = $v", ("$v", videoId));
        Changes |= LibraryChange.Lyrics;
    }

    /// <summary>Снимок своих версий на сервере.</summary>
    public Dictionary<string, Lyrics.LyricsSnapshot> SyncedLyrics() => Query("SELECT video_id, rev, hash FROM synced_lyrics",
        r => (Id: r.GetString(0), Snapshot: new Lyrics.LyricsSnapshot(r.GetInt64(1), r.GetString(2)))).ToDictionary(p => p.Id, p => p.Snapshot, StringComparer.Ordinal);

    public void SetSyncedLyrics(string videoId, long rev, string hash) =>
        Exec("INSERT OR REPLACE INTO synced_lyrics (video_id, rev, hash) VALUES ($v, $r, $h)", ("$v", videoId), ("$r", rev), ("$h", hash));

    public void ForgetSyncedLyrics(string videoId) => Exec("DELETE FROM synced_lyrics WHERE video_id = $v", ("$v", videoId));

    /// <summary>Лайк с сервера: время лайка — серверное, снятый лайк — снятие.</summary>
    public void SetLike(string videoId, long? likedAt)
    {
        Exec("UPDATE tracks SET liked_at = $at WHERE video_id = $id", ("$at", likedAt), ("$id", videoId));
        if (likedAt is not null) Exec("INSERT OR IGNORE INTO synced_likes (video_id) VALUES ($id)", ("$id", videoId));
        else Exec("DELETE FROM synced_likes WHERE video_id = $id", ("$id", videoId));
        Changes |= LibraryChange.Likes;
    }

    /// <summary>Закладка с сервера: у новой — снимок названия и обложки.</summary>
    public void SetBookmark(string type, string browseId, long? bookmarkedAt, string? title, string? subtitle, string? thumbnailUrl, string? year)
    {
        if (type == "album")
        {
            if (bookmarkedAt is not null)
                Exec("INSERT OR IGNORE INTO albums (browse_id, title, artists_text, year, thumbnail_url) VALUES ($id, $t, $s, $y, $th)",
                    ("$id", browseId), ("$t", title), ("$s", subtitle), ("$y", year), ("$th", thumbnailUrl));
            Exec("UPDATE albums SET bookmarked_at = $at WHERE browse_id = $id", ("$at", bookmarkedAt), ("$id", browseId));
        }
        else
        {
            if (bookmarkedAt is not null)
                Exec("INSERT OR IGNORE INTO artists (browse_id, name, thumbnail_url) VALUES ($id, $t, $th)", ("$id", browseId), ("$t", title), ("$th", thumbnailUrl));
            Exec("UPDATE artists SET bookmarked_at = $at WHERE browse_id = $id", ("$at", bookmarkedAt), ("$id", browseId));
        }
        if (bookmarkedAt is not null) Exec("INSERT OR IGNORE INTO synced_bookmarks (type, browse_id) VALUES ($t, $id)", ("$t", type), ("$id", browseId));
        else Exec("DELETE FROM synced_bookmarks WHERE type = $t AND browse_id = $id", ("$t", type), ("$id", browseId));
        Changes |= LibraryChange.Bookmarks;
    }
}
