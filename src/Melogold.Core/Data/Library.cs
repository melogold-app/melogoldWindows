using System.Text.Json;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

[Flags]
public enum LibraryChange
{
    None = 0,
    Likes = 1,
    Playlists = 2,
    Bookmarks = 4,
    History = 8,
    Tracks = 16,
    Searches = 32,
    Blocks = 64,
    Lyrics = 128,
    Downloads = 256,
    All = Likes | Playlists | Bookmarks | History | Tracks | Searches | Blocks | Lyrics | Downloads,
}

/// <summary>Свой плейлист (в Библиотеке): <see cref="SyncId"/> — UUID сервера, если плейлист синхронизирован.</summary>
public sealed record LocalPlaylist(long Id, string? SyncId, string Name, string? BrowseId, string? ThumbnailUrl, long CreatedAt, int TrackCount, IReadOnlyList<string> Mosaic);

public sealed record HistoryEntry(Track Track, long PlayedAt);

/// <summary>
/// Чьи прослушивания показывает История (tasks/0002 §3.5): все; это устройство — события без <c>device_id</c> и с
/// <c>device_id</c> этого устройства; другое устройство аккаунта.
/// </summary>
public sealed record HistoryDevice(string? DeviceId, bool ThisDevice)
{
    public static readonly HistoryDevice All = new(null, false);

    public static HistoryDevice This(string? currentDeviceId) => new(currentDeviceId, true);

    public static HistoryDevice Other(string deviceId) => new(deviceId, false);

    public bool IsAll => DeviceId is null && !ThisDevice;

    /// <summary>Условие на <c>play_events</c> и его параметры.</summary>
    internal (string Sql, (string, object?)[] Parameters) Where() => this switch
    {
        { IsAll: true } => ("1 = 1", []),
        { ThisDevice: true, DeviceId: null } => ("device_id IS NULL", []),
        { ThisDevice: true } => ("(device_id IS NULL OR device_id = $device)", [("$device", DeviceId)]),
        _ => ("device_id = $device", [("$device", DeviceId)]),
    };
}

public sealed record TopEntry(Track Track, long PlayTimeMs);

/// <summary>Трек «Всех треков»: когда слушали последний раз (null — не слушали) и сколько всего.</summary>
public sealed record AllTracksEntry(Track Track, long? LastPlayedAt, long PlayTimeMs);

/// <summary>
/// Текст трека (Android <c>Lyrics</c>): у каждой стороны null — ещё не искали, "" — не нашли. Источник —
/// <see cref="LyricsSources"/>. <see cref="OffsetMs"/> — сдвиг синхронного текста у этого трека (положительный — раньше),
/// <see cref="Language"/> — BCP 47, если известен.
/// </summary>
/// <summary>
/// Текст трека. <paramref name="Chosen"/> — пользователь сам выбрал его вместо найденного автоматически («Найти другой
/// текст») или он пришёл с сервера своей версией: такой текст — свой, как набранный и импортированный, и уходит на
/// сервер со своим настоящим источником (API §4.10: «выбранные вместо найденного автоматически»).
/// </summary>
public sealed record StoredLyrics(string? Synced, string? Plain, string? SyncedSource, string? PlainSource, long OffsetMs = 0, string? Language = null,
    bool Chosen = false);

/// <summary>Откуда текст — словарь сервера (docs/LYRICS-SYNC.md §2) и <see cref="Melogold"/>, общий текст сообщества.</summary>
public static class LyricsSources
{
    public const string YouTubeMusic = "youtube_music";
    public const string LrcLib = "lrclib";
    public const string KuGou = "kugou";
    public const string File = "file";
    public const string User = "user";

    /// <summary>Общий текст другого пользователя с сервера: показывается, но своим не становится.</summary>
    public const string Melogold = "melogold";
}

/// <summary>Трек плейлиста с порядком и ключом сервера.</summary>
public sealed record PlaylistEntry(Track Track, int Position, string? SortKey);

/// <summary>
/// Все записи в библиотеку идут отсюда (REWRITE §0, принцип 7): экраны, плеер и синхронизация. После каждой записи —
/// <see cref="Changed"/>, по нему обновляются экраны и через 2 с запускается синхронизация.
/// </summary>
public sealed class Library(LibraryDatabase db)
{
    public LibraryDatabase Database { get; } = db;

    public event Action<LibraryChange>? Changed;

    public void Notify(LibraryChange change)
    {
        if (change != LibraryChange.None) Changed?.Invoke(change);
    }

    // ---------- Треки ----------

    internal const string TrackColumns = "video_id, title, artists_text, artists_json, album_id, album_title, duration_ms, duration_text, thumbnail_url, explicit, video_type, metadata_stub, liked_at, total_play_ms";

    internal static Track ReadTrack(SqliteDataReader r, int o = 0)
    {
        var artistsJson = r.IsDBNull(o + 3) ? null : r.GetString(o + 3);
        IReadOnlyList<ArtistRef> artists = [];
        if (artistsJson is not null)
        {
            try
            {
                artists = JsonSerializer.Deserialize<List<ArtistRef>>(artistsJson, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
            }
        }
        return new Track
        {
            VideoId = r.GetString(o),
            Title = r.GetString(o + 1),
            ArtistsText = r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
            Artists = artists,
            AlbumId = r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
            AlbumTitle = r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
            DurationMs = r.IsDBNull(o + 6) ? null : r.GetInt64(o + 6),
            DurationText = r.IsDBNull(o + 7) ? null : r.GetString(o + 7),
            ThumbnailUrl = r.IsDBNull(o + 8) ? null : r.GetString(o + 8),
            Explicit = r.GetInt64(o + 9) != 0,
            VideoType = r.IsDBNull(o + 10) ? null : r.GetString(o + 10),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Вставляет трек или обновляет его метаданные: пустые поля новой версии не затирают известные, заглушка
    /// (название = videoId) получает настоящее название.
    /// </summary>
    internal static void UpsertTrack(SqliteConnection c, SqliteTransaction t, Track track, bool stub = false)
    {
        LibraryDatabase.Exec(c, t, """
            INSERT INTO tracks (video_id, title, artists_text, artists_json, album_id, album_title, duration_ms, duration_text,
                                thumbnail_url, explicit, video_type, metadata_stub, created_at)
            VALUES ($id, $title, $artists, $artistsJson, $albumId, $albumTitle, $durationMs, $durationText, $thumb, $explicit, $type, $stub, $now)
            ON CONFLICT(video_id) DO UPDATE SET
                title = CASE WHEN $stub = 0 THEN excluded.title ELSE tracks.title END,
                artists_text = COALESCE(excluded.artists_text, tracks.artists_text),
                artists_json = COALESCE(excluded.artists_json, tracks.artists_json),
                album_id = COALESCE(excluded.album_id, tracks.album_id),
                album_title = COALESCE(excluded.album_title, tracks.album_title),
                duration_ms = COALESCE(excluded.duration_ms, tracks.duration_ms),
                duration_text = COALESCE(excluded.duration_text, tracks.duration_text),
                thumbnail_url = COALESCE(excluded.thumbnail_url, tracks.thumbnail_url),
                explicit = MAX(excluded.explicit, tracks.explicit),
                video_type = COALESCE(tracks.video_type, excluded.video_type),
                metadata_stub = CASE WHEN $stub = 0 THEN 0 ELSE tracks.metadata_stub END;
            """,
            ("$id", track.VideoId), ("$title", track.Title), ("$artists", track.ArtistsText),
            ("$artistsJson", track.Artists.Count > 0 ? JsonSerializer.Serialize(track.Artists, JsonOptions) : null),
            ("$albumId", track.AlbumId), ("$albumTitle", track.AlbumTitle), ("$durationMs", track.DurationMs ?? Durations.ParseText(track.DurationText)),
            ("$durationText", track.DurationText ?? (track.DurationMs is { } ms ? Durations.Format(ms) : null)),
            ("$thumb", track.ThumbnailUrl), ("$explicit", track.Explicit ? 1 : 0), ("$type", track.VideoType), ("$stub", stub ? 1 : 0),
            ("$now", IsoTime.NowMs()));
    }

    public void SaveTracks(IEnumerable<Track> tracks)
    {
        Database.Write((c, t) =>
        {
            foreach (var track in tracks) UpsertTrack(c, t, track);
        });
    }

    public Track? GetTrack(string videoId) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"SELECT {TrackColumns} FROM tracks WHERE video_id = $id";
        command.Parameters.AddWithValue("$id", videoId);
        using var r = command.ExecuteReader();
        return r.Read() ? ReadTrack(r) : null;
    });

    private static List<Track> Tracks(SqliteConnection c, string sql, params (string, object?)[] parameters)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = command.ExecuteReader();
        var list = new List<Track>();
        while (r.Read()) list.Add(ReadTrack(r));
        return list;
    }

    // ---------- Избранное ----------

    public bool IsLiked(string videoId) => Database.Read(c =>
        LibraryDatabase.Scalar(c, "SELECT liked_at IS NOT NULL FROM tracks WHERE video_id = $id", ("$id", videoId)) is long v && v != 0);

    public HashSet<string> LikedIds() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT video_id FROM tracks WHERE liked_at IS NOT NULL";
        using var r = command.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    });

    public List<Track> Favorites() => Database.Read(c => Tracks(c, $"SELECT {TrackColumns} FROM tracks WHERE liked_at IS NOT NULL ORDER BY liked_at DESC"));

    public void SetLiked(Track track, bool liked)
    {
        Database.Write((c, t) =>
        {
            UpsertTrack(c, t, track);
            LibraryDatabase.Exec(c, t,
                liked ? "UPDATE tracks SET liked_at = COALESCE(liked_at, $now) WHERE video_id = $id" : "UPDATE tracks SET liked_at = NULL WHERE video_id = $id",
                ("$id", track.VideoId), ("$now", IsoTime.NowMs()));
        });
        Notify(LibraryChange.Likes);
    }

    /// <summary>♡ сразу у нескольких треков (действия с выделенным): одна запись, одно уведомление.</summary>
    public void SetLiked(IReadOnlyList<Track> tracks, bool liked)
    {
        if (tracks.Count == 0) return;
        var now = IsoTime.NowMs();
        Database.Write((c, t) =>
        {
            foreach (var track in tracks)
            {
                UpsertTrack(c, t, track);
                LibraryDatabase.Exec(c, t,
                    liked ? "UPDATE tracks SET liked_at = COALESCE(liked_at, $now) WHERE video_id = $id" : "UPDATE tracks SET liked_at = NULL WHERE video_id = $id",
                    ("$id", track.VideoId), ("$now", now));
            }
        });
        Notify(LibraryChange.Likes);
    }

    // ---------- Альбомы и исполнители ----------

    public bool IsAlbumSaved(string browseId) => Database.Read(c =>
        LibraryDatabase.Scalar(c, "SELECT bookmarked_at IS NOT NULL FROM albums WHERE browse_id = $id", ("$id", browseId)) is long v && v != 0);

    public void SetAlbumSaved(AlbumItem album, bool saved)
    {
        Database.Write((c, t) => UpsertAlbum(c, t, album, saved ? IsoTime.NowMs() : null, true));
        Notify(LibraryChange.Bookmarks);
    }

    internal static void UpsertAlbum(SqliteConnection c, SqliteTransaction t, AlbumItem album, long? bookmarkedAt, bool setBookmark)
    {
        LibraryDatabase.Exec(c, t, """
            INSERT INTO albums (browse_id, title, artists_text, year, thumbnail_url, playlist_id, bookmarked_at)
            VALUES ($id, $title, $artists, $year, $thumb, $playlist, $at)
            ON CONFLICT(browse_id) DO UPDATE SET
                title = COALESCE(excluded.title, albums.title),
                artists_text = COALESCE(excluded.artists_text, albums.artists_text),
                year = COALESCE(excluded.year, albums.year),
                thumbnail_url = COALESCE(excluded.thumbnail_url, albums.thumbnail_url),
                playlist_id = COALESCE(excluded.playlist_id, albums.playlist_id),
                bookmarked_at = CASE WHEN $set = 1 THEN (CASE WHEN $at IS NULL THEN NULL ELSE COALESCE(albums.bookmarked_at, $at) END) ELSE albums.bookmarked_at END;
            """,
            ("$id", album.BrowseId), ("$title", album.Title), ("$artists", album.ArtistsText), ("$year", album.Year),
            ("$thumb", album.ThumbnailUrl), ("$playlist", album.PlaylistId), ("$at", bookmarkedAt), ("$set", setBookmark ? 1 : 0));
    }

    public List<AlbumItem> SavedAlbums() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT browse_id, title, artists_text, year, thumbnail_url, playlist_id FROM albums WHERE bookmarked_at IS NOT NULL ORDER BY bookmarked_at DESC";
        using var r = command.ExecuteReader();
        var list = new List<AlbumItem>();
        while (r.Read())
        {
            list.Add(new AlbumItem
            {
                BrowseId = r.GetString(0),
                Title = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                ArtistsText = r.IsDBNull(2) ? null : r.GetString(2),
                Year = r.IsDBNull(3) ? null : r.GetString(3),
                ThumbnailUrl = r.IsDBNull(4) ? null : r.GetString(4),
                PlaylistId = r.IsDBNull(5) ? null : r.GetString(5),
            });
        }
        return list;
    });

    public bool IsArtistSaved(string browseId) => Database.Read(c =>
        LibraryDatabase.Scalar(c, "SELECT bookmarked_at IS NOT NULL FROM artists WHERE browse_id = $id", ("$id", browseId)) is long v && v != 0);

    public void SetArtistSaved(ArtistItem artist, bool saved)
    {
        Database.Write((c, t) => UpsertArtist(c, t, artist, saved ? IsoTime.NowMs() : null, true));
        Notify(LibraryChange.Bookmarks);
    }

    internal static void UpsertArtist(SqliteConnection c, SqliteTransaction t, ArtistItem artist, long? bookmarkedAt, bool setBookmark)
    {
        LibraryDatabase.Exec(c, t, """
            INSERT INTO artists (browse_id, name, thumbnail_url, is_channel, bookmarked_at)
            VALUES ($id, $name, $thumb, $channel, $at)
            ON CONFLICT(browse_id) DO UPDATE SET
                name = COALESCE(excluded.name, artists.name),
                thumbnail_url = COALESCE(excluded.thumbnail_url, artists.thumbnail_url),
                is_channel = excluded.is_channel,
                bookmarked_at = CASE WHEN $set = 1 THEN (CASE WHEN $at IS NULL THEN NULL ELSE COALESCE(artists.bookmarked_at, $at) END) ELSE artists.bookmarked_at END;
            """,
            ("$id", artist.BrowseId), ("$name", artist.Name), ("$thumb", artist.ThumbnailUrl), ("$channel", artist.IsChannel ? 1 : 0),
            ("$at", bookmarkedAt), ("$set", setBookmark ? 1 : 0));
    }

    public List<ArtistItem> SavedArtists() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT browse_id, name, thumbnail_url, is_channel FROM artists WHERE bookmarked_at IS NOT NULL ORDER BY bookmarked_at DESC";
        using var r = command.ExecuteReader();
        var list = new List<ArtistItem>();
        while (r.Read())
        {
            list.Add(new ArtistItem
            {
                BrowseId = r.GetString(0),
                Name = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                ThumbnailUrl = r.IsDBNull(2) ? null : r.GetString(2),
                IsChannel = r.GetInt64(3) != 0,
            });
        }
        return list;
    });

    // ---------- Плейлисты ----------

    public List<LocalPlaylist> Playlists() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.sync_id, p.name, p.browse_id, p.thumbnail_url, p.created_at,
                   (SELECT COUNT(*) FROM playlist_items i WHERE i.playlist_id = p.id)
            FROM playlists p ORDER BY p.created_at DESC, p.id DESC
            """;
        using var r = command.ExecuteReader();
        var list = new List<(long, string?, string, string?, string?, long, int)>();
        while (r.Read())
        {
            list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt32(6)));
        }
        return list.Select(p => new LocalPlaylist(p.Item1, p.Item2, p.Item3, p.Item4, p.Item5, p.Item6, p.Item7, Mosaic(c, p.Item1))).ToList();
    });

    private static List<string> Mosaic(SqliteConnection c, long playlistId)
    {
        using var command = c.CreateCommand();
        command.CommandText = """
            SELECT t.thumbnail_url FROM playlist_items i JOIN tracks t ON t.video_id = i.video_id
            WHERE i.playlist_id = $id AND t.thumbnail_url IS NOT NULL ORDER BY i.position LIMIT 4
            """;
        command.Parameters.AddWithValue("$id", playlistId);
        using var r = command.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public LocalPlaylist? GetPlaylist(long id) => Playlists().FirstOrDefault(p => p.Id == id);

    public List<Track> PlaylistTracks(long playlistId) => Database.Read(c => Tracks(c,
        $"SELECT {string.Join(", ", TrackColumns.Split(", ").Select(x => "t." + x))} FROM playlist_items i JOIN tracks t ON t.video_id = i.video_id WHERE i.playlist_id = $id ORDER BY i.position",
        ("$id", playlistId)));

    public long CreatePlaylist(string name, IEnumerable<Track>? tracks = null, string? browseId = null, string? thumbnailUrl = null)
    {
        var id = Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "INSERT INTO playlists (name, browse_id, thumbnail_url, created_at) VALUES ($name, $browse, $thumb, $now)",
                ("$name", Utf16.Truncate(name.Trim().Length == 0 ? "Без названия" : name.Trim(), 200)), ("$browse", browseId), ("$thumb", thumbnailUrl), ("$now", IsoTime.NowMs()));
            var newId = (long)LibraryDatabase.Scalar(c, "SELECT last_insert_rowid()")!;
            if (tracks is not null) AppendItems(c, t, newId, tracks);
            return newId;
        });
        Notify(LibraryChange.Playlists);
        return id;
    }

    public void RenamePlaylist(long id, string name)
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "UPDATE playlists SET name = $name WHERE id = $id",
            ("$name", Utf16.Truncate(name.Trim(), 200)), ("$id", id)));
        Notify(LibraryChange.Playlists);
    }

    public void DeletePlaylist(long id)
    {
        Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "DELETE FROM playlist_items WHERE playlist_id = $id", ("$id", id));
            LibraryDatabase.Exec(c, t, "DELETE FROM playlists WHERE id = $id", ("$id", id));
        });
        Notify(LibraryChange.Playlists);
    }

    /// <summary>Добавить в конец; трек встречается в плейлисте не больше одного раза (DESIGN §3.2). Возвращает число добавленных.</summary>
    public int AddToPlaylist(long playlistId, IEnumerable<Track> tracks)
    {
        var added = Database.Write((c, t) => AppendItems(c, t, playlistId, tracks));
        if (added > 0) Notify(LibraryChange.Playlists);
        return added;
    }

    private static int AppendItems(SqliteConnection c, SqliteTransaction t, long playlistId, IEnumerable<Track> tracks)
    {
        var next = Convert.ToInt32(LibraryDatabase.Scalar(c, "SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_items WHERE playlist_id = $id", ("$id", playlistId)));
        var added = 0;
        var now = IsoTime.NowMs();
        foreach (var track in tracks)
        {
            UpsertTrack(c, t, track);
            added += LibraryDatabase.Exec(c, t,
                "INSERT OR IGNORE INTO playlist_items (playlist_id, video_id, position, added_at) VALUES ($p, $v, $pos, $now)",
                ("$p", playlistId), ("$v", track.VideoId), ("$pos", next + added), ("$now", now));
        }
        return added;
    }

    public void RemoveFromPlaylist(long playlistId, string videoId)
    {
        Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "DELETE FROM playlist_items WHERE playlist_id = $p AND video_id = $v", ("$p", playlistId), ("$v", videoId));
            Renumber(c, t, playlistId);
        });
        Notify(LibraryChange.Playlists);
    }

    /// <summary>Перенести трек на место <paramref name="newIndex"/> (0..n-1).</summary>
    public void MoveInPlaylist(long playlistId, string videoId, int newIndex)
    {
        Database.Write((c, t) =>
        {
            var order = VideoIds(c, playlistId);
            if (!order.Remove(videoId)) return;
            order.Insert(Math.Clamp(newIndex, 0, order.Count), videoId);
            for (var i = 0; i < order.Count; i++)
                LibraryDatabase.Exec(c, t, "UPDATE playlist_items SET position = $pos WHERE playlist_id = $p AND video_id = $v", ("$pos", i), ("$p", playlistId), ("$v", order[i]));
        });
        Notify(LibraryChange.Playlists);
    }

    internal static List<string> VideoIds(SqliteConnection c, long playlistId)
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT video_id FROM playlist_items WHERE playlist_id = $id ORDER BY position";
        command.Parameters.AddWithValue("$id", playlistId);
        using var r = command.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static void Renumber(SqliteConnection c, SqliteTransaction t, long playlistId)
    {
        var order = VideoIds(c, playlistId);
        for (var i = 0; i < order.Count; i++)
            LibraryDatabase.Exec(c, t, "UPDATE playlist_items SET position = $pos WHERE playlist_id = $p AND video_id = $v", ("$pos", i), ("$p", playlistId), ("$v", order[i]));
    }

    /// <summary>Плейлисты, в которых есть трек (галочки в «Добавить в плейлист…»).</summary>
    public HashSet<long> PlaylistsContaining(string videoId) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT playlist_id FROM playlist_items WHERE video_id = $v";
        command.Parameters.AddWithValue("$v", videoId);
        using var r = command.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    });

    // ---------- История (DESIGN §3.11) ----------

    /// <summary>
    /// Прослушивание (сеанс ≥ 5 с): одной транзакцией трек, событие с UUID и счётчик времени (DESIGN §3.11.4).
    /// </summary>
    public void RecordPlay(Track track, long playTimeMs, long endedAtMs)
    {
        if (playTimeMs < 5000) return;
        Database.Write((c, t) =>
        {
            UpsertTrack(c, t, track);
            LibraryDatabase.Exec(c, t, "INSERT INTO play_events (event_id, video_id, played_at, play_time_ms) VALUES ($e, $v, $at, $ms)",
                ("$e", Guid.NewGuid().ToString()), ("$v", track.VideoId), ("$at", endedAtMs), ("$ms", playTimeMs));
            LibraryDatabase.Exec(c, t, "UPDATE tracks SET total_play_ms = total_play_ms + $ms WHERE video_id = $v", ("$ms", playTimeMs), ("$v", track.VideoId));
        });
        Notify(LibraryChange.History);
    }

    /// <summary>«Недавние»: последние 100 разных треков по последнему прослушиванию (DESIGN §3.11.7) выбранного устройства.</summary>
    public List<HistoryEntry> RecentHistory(int limit = 100, HistoryDevice? device = null) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        var (where, parameters) = (device ?? HistoryDevice.All).Where();
        command.CommandText = $"""
            SELECT {string.Join(", ", TrackColumns.Split(", ").Select(x => "t." + x))}, h.last
            FROM (SELECT video_id, MAX(played_at) AS last FROM play_events WHERE {where} GROUP BY video_id ORDER BY last DESC LIMIT $limit) h
            JOIN tracks t ON t.video_id = h.video_id ORDER BY h.last DESC
            """;
        command.Parameters.AddWithValue("$limit", limit);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = command.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read()) list.Add(new HistoryEntry(ReadTrack(r), r.GetInt64(14)));
        return list;
    });

    /// <summary>
    /// «Чаще всего» за период: Σ времени событий выбранного устройства; за всё время у «Все устройства» — общее время
    /// трека с сервера (<c>playStats</c>, DESIGN §3.11.7).
    /// </summary>
    public List<TopEntry> MostPlayed(long? sinceMs, int limit = 100, HistoryDevice? device = null) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        var columns = string.Join(", ", TrackColumns.Split(", ").Select(x => "t." + x));
        var (where, parameters) = (device ?? HistoryDevice.All).Where();
        command.CommandText = sinceMs is null && (device ?? HistoryDevice.All).IsAll
            ? $"SELECT {columns}, t.total_play_ms FROM tracks t WHERE t.total_play_ms > 0 ORDER BY t.total_play_ms DESC LIMIT $limit"
            : $"""
               SELECT {columns}, s.total FROM (SELECT video_id, SUM(play_time_ms) AS total FROM play_events WHERE played_at >= $since AND {where} GROUP BY video_id ORDER BY total DESC LIMIT $limit) s
               JOIN tracks t ON t.video_id = s.video_id ORDER BY s.total DESC
               """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$since", sinceMs ?? 0);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = command.ExecuteReader();
        var list = new List<TopEntry>();
        while (r.Read()) list.Add(new TopEntry(ReadTrack(r), r.GetInt64(14)));
        return list;
    });

    /// <summary>
    /// «Очистить историю» на всех устройствах: события удаляются, счётчики остаются, как в ViTune (DESIGN §3.11.5);
    /// синхронизация отправит <c>history.clear</c>.
    /// </summary>
    public void ClearHistory()
    {
        var now = IsoTime.NowMs();
        Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "DELETE FROM play_events WHERE played_at <= $now", ("$now", now));
            LibraryDatabase.Exec(c, t, "INSERT INTO history_ops (op_id, kind, video_id, events_before) VALUES ($id, 'history.clear', NULL, $now)",
                ("$id", Guid.NewGuid().ToString()), ("$now", now));
        });
        Notify(LibraryChange.History);
    }

    /// <summary>
    /// «Убрать из истории» на всех устройствах (tasks/0008): события трека удаляются и общее время обнуляется — трек
    /// пропадает и из Истории, и из «Чаще всего», как на Android и Apple; синхронизация отправит <c>history.forget</c> с
    /// <c>resetTotal: true</c>. <paramref name="before"/> — когда нажали: у действия есть «Отменить», и прослушивания,
    /// записанные за эти секунды, не должны стереться на всех устройствах.
    /// </summary>
    public void RemoveFromHistory(string videoId, long? before = null)
    {
        var now = before ?? IsoTime.NowMs();
        Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "DELETE FROM play_events WHERE video_id = $v AND played_at <= $now", ("$v", videoId), ("$now", now));
            LibraryDatabase.Exec(c, t, "UPDATE tracks SET total_play_ms = 0 WHERE video_id = $v", ("$v", videoId));
            LibraryDatabase.Exec(c, t, "INSERT INTO history_ops (op_id, kind, video_id, events_before) VALUES ($id, 'history.forget', $v, $now)",
                ("$id", Guid.NewGuid().ToString()), ("$v", videoId), ("$now", now));
        });
        Notify(LibraryChange.History);
    }

    /// <summary>Сколько прослушиваний в Истории (для «Очистить историю…»).</summary>
    public long PlayCount() => Database.Read(c => Convert.ToInt64(LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM play_events"), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Другие устройства, чьи прослушивания есть в Истории (для фильтра).</summary>
    public List<string> HistoryDeviceIds() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT DISTINCT device_id FROM play_events WHERE device_id IS NOT NULL";
        using var r = command.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    /// <summary>Затравки «Для вас» (REWRITE §4.10.5): последний лайк, самый частый за 30 дней, последний прослушанный.</summary>
    public List<Track> ForYouSeeds()
    {
        var seeds = new List<Track>();
        var favorites = Favorites();
        if (favorites.Count > 0) seeds.Add(favorites[0]);
        var top = MostPlayed(IsoTime.NowMs() - 30L * 24 * 3600 * 1000, 1);
        if (top.Count > 0) seeds.Add(top[0].Track);
        var recent = RecentHistory(1);
        if (recent.Count > 0) seeds.Add(recent[0].Track);
        return seeds.DistinctBy(t => t.VideoId).ToList();
    }

    // ---------- Поиск ----------

    public void AddSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "INSERT INTO search_history (query, searched_at) VALUES ($q, $now) ON CONFLICT(query) DO UPDATE SET searched_at = excluded.searched_at",
                ("$q", Utf16.Truncate(query, 200)), ("$now", IsoTime.NowMs()));
            LibraryDatabase.Exec(c, t, "DELETE FROM search_history WHERE query NOT IN (SELECT query FROM search_history ORDER BY searched_at DESC LIMIT 50)");
        });
        Notify(LibraryChange.Searches);
    }

    public List<string> RecentSearches(int limit = 12) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT query FROM search_history ORDER BY searched_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var r = command.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    public void RemoveSearch(string query)
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "DELETE FROM search_history WHERE query = $q", ("$q", query)));
        Notify(LibraryChange.Searches);
    }

    public void ClearSearches()
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "DELETE FROM search_history"));
        Notify(LibraryChange.Searches);
    }

    /// <summary>Поиск по своей библиотеке («В библиотеке» при вводе).</summary>
    public List<Track> SearchLibrary(string query, int limit = 5)
    {
        var pattern = "%" + query.Trim().Replace("%", "").Replace("_", "") + "%";
        return Database.Read(c => Tracks(c,
            $"""
             SELECT {TrackColumns} FROM tracks
             WHERE (liked_at IS NOT NULL OR total_play_ms > 0 OR video_id IN (SELECT video_id FROM playlist_items))
               AND (title LIKE $q OR artists_text LIKE $q)
             ORDER BY liked_at IS NULL, total_play_ms DESC LIMIT $limit
             """, ("$q", pattern), ("$limit", limit)));
    }

    // ---------- Тексты ----------

    /// <summary>Текст трека из кэша: null у стороны — ещё не искали, пустая строка — искали и не нашли.</summary>
    public StoredLyrics? GetLyrics(string videoId) => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"SELECT {LyricsColumns} FROM lyrics WHERE video_id = $v";
        command.Parameters.AddWithValue("$v", videoId);
        using var r = command.ExecuteReader();
        return r.Read() ? ReadLyrics(r) : null;
    });

    internal const string LyricsColumns = "synced, plain, source, plain_source, offset_ms, language, chosen";

    internal static StoredLyrics ReadLyrics(SqliteDataReader r, int o = 0) => new(
        r.IsDBNull(o) ? null : r.GetString(o), r.IsDBNull(o + 1) ? null : r.GetString(o + 1),
        r.IsDBNull(o + 2) ? null : r.GetString(o + 2), r.IsDBNull(o + 3) ? null : r.GetString(o + 3), r.GetInt64(o + 4),
        r.IsDBNull(o + 5) ? null : r.GetString(o + 5), r.GetInt64(o + 6) != 0);

    /// <summary>Записать текст; свой текст через 2 с уходит на сервер (<see cref="LibraryChange.Lyrics"/>).</summary>
    public void SaveLyrics(string videoId, StoredLyrics lyrics)
    {
        Database.Write((c, t) => WriteLyrics(c, t, videoId, lyrics));
        Notify(LibraryChange.Lyrics);
    }

    internal static void WriteLyrics(SqliteConnection c, SqliteTransaction t, string videoId, StoredLyrics lyrics) =>
        LibraryDatabase.Exec(c, t, """
            INSERT OR REPLACE INTO lyrics (video_id, synced, plain, source, plain_source, offset_ms, language, chosen, fetched_at)
            VALUES ($v, $s, $p, $src, $psrc, $offset, $lang, $chosen, $now)
            """,
            ("$v", videoId), ("$s", lyrics.Synced), ("$p", lyrics.Plain), ("$src", lyrics.SyncedSource), ("$psrc", lyrics.PlainSource),
            ("$offset", lyrics.OffsetMs), ("$lang", lyrics.Language), ("$chosen", lyrics.Chosen ? 1 : 0), ("$now", IsoTime.NowMs()));

    // Выбранный пользователем текст — не кэш: «Очистить» его не трогает
    private const string FetchedOnly = "COALESCE(source, '') NOT IN ('file', 'user') AND COALESCE(plain_source, '') NOT IN ('file', 'user') AND chosen = 0";

    /// <summary>Размер найденных в сети текстов, байт (кэш: их можно найти снова).</summary>
    public long FetchedLyricsSize() => Database.Read(c =>
        Convert.ToInt64(LibraryDatabase.Scalar(c, $"SELECT COALESCE(SUM(LENGTH(COALESCE(synced, '')) + LENGTH(COALESCE(plain, ''))), 0) FROM lyrics WHERE {FetchedOnly}"), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Очистка кэша: найденные в сети тексты забываются, свои и импортированные остаются.</summary>
    public void ClearFetchedLyrics() => Database.Write((c, t) => LibraryDatabase.Exec(c, t, $"DELETE FROM lyrics WHERE {FetchedOnly}"));

    // ---------- «Не показывать» ----------

    public HashSet<string> HiddenTracks() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT key FROM content_blocks WHERE type = 'track'";
        using var r = command.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    });

    public void SetTrackHidden(Track track, bool hidden)
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, hidden
                ? "INSERT OR REPLACE INTO content_blocks (type, key, level, title, subtitle, thumbnail_url, blocked_at) VALUES ('track', $k, 'hide', $title, $sub, $thumb, $now)"
                : "DELETE FROM content_blocks WHERE type = 'track' AND key = $k",
            ("$k", track.VideoId), ("$title", track.Title), ("$sub", track.ArtistsText), ("$thumb", track.ThumbnailUrl), ("$now", IsoTime.NowMs())));
        Notify(LibraryChange.Blocks);
    }

    /// <summary>«Сбросить черный список»: скрытые треки снова показываются.</summary>
    public void ClearHiddenTracks()
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "DELETE FROM content_blocks WHERE type = 'track'"));
        Notify(LibraryChange.Blocks);
    }

    public List<Track> HiddenTrackList() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT key, title, subtitle, thumbnail_url FROM content_blocks WHERE type = 'track' ORDER BY blocked_at DESC";
        using var r = command.ExecuteReader();
        var list = new List<Track>();
        while (r.Read())
        {
            list.Add(new Track
            {
                VideoId = r.GetString(0),
                Title = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                ArtistsText = r.IsDBNull(2) ? null : r.GetString(2),
                ThumbnailUrl = r.IsDBNull(3) ? null : r.GetString(3),
            });
        }
        return list;
    });

    // ---------- Состояние приложения ----------

    public string? GetState(string key) => Database.Read(c => LibraryDatabase.Scalar(c, "SELECT value FROM app_state WHERE key = $k", ("$k", key)) as string);

    public void SetState(string key, string? value) => Database.Write((c, t) =>
        LibraryDatabase.Exec(c, t, value is null ? "DELETE FROM app_state WHERE key = $k" : "INSERT OR REPLACE INTO app_state (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value)));

    /// <summary>Отпечаток библиотеки: меняется от любой правки Избранного, плейлистов и закладок (в том числе перестановки).</summary>
    public string LibraryFingerprint() => Database.Read(c => string.Join("|",
        LibraryDatabase.Scalar(c, "SELECT COUNT(*) || ':' || COALESCE(SUM(liked_at), 0) FROM tracks WHERE liked_at IS NOT NULL"),
        LibraryDatabase.Scalar(c, "SELECT COUNT(*) || ':' || COALESCE(SUM(length(name) + id), 0) || ':' || COALESCE(SUM(length(thumbnail_url)), 0) FROM playlists"),
        LibraryDatabase.Scalar(c, "SELECT COUNT(*) || ':' || COALESCE(SUM(position * length(video_id) + playlist_id), 0) FROM playlist_items"),
        LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM albums WHERE bookmarked_at IS NOT NULL"),
        LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM artists WHERE bookmarked_at IS NOT NULL")));

    /// <summary>Сколько всего в библиотеке — для диалога первой синхронизации.</summary>
    /// <summary>
    /// Что входит во «Все треки» (tasks/0005 §2): прослушанное, лайкнутое, лежащее в своих плейлистах; не скрытое. Треки
    /// альбомов, которые только открывали, — нет: копии ViTune хранят их тысячами.
    /// </summary>
    // ---------- Загрузки ----------

    /// <summary>«Скачать»: трек — в библиотеку (название, обложка для «Скачанного»), в список загрузок.</summary>
    public void AddDownload(Track track)
    {
        Database.Write((c, t) =>
        {
            UpsertTrack(c, t, track);
            LibraryDatabase.Exec(c, t, "INSERT OR IGNORE INTO downloads (video_id, added_at) VALUES ($v, $now)",
                ("$v", track.VideoId), ("$now", IsoTime.NowMs()));
        });
        Changed?.Invoke(LibraryChange.Downloads);
    }

    /// <summary>«Удалить загрузку»: трек остаётся в библиотеке, но без сети играть не будет.</summary>
    public void RemoveDownload(string videoId)
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "DELETE FROM downloads WHERE video_id = $v", ("$v", videoId)));
        Changed?.Invoke(LibraryChange.Downloads);
    }

    public void RemoveAllDownloads()
    {
        Database.Write((c, t) => LibraryDatabase.Exec(c, t, "DELETE FROM downloads"));
        Changed?.Invoke(LibraryChange.Downloads);
    }

    /// <summary>Скачанные треки, сначала недавние.</summary>
    public List<Track> Downloads() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", TrackColumns.Split(", ").Select(x => "t." + x))}
            FROM downloads d JOIN tracks t ON t.video_id = d.video_id
            ORDER BY d.added_at DESC
            """;
        using var r = command.ExecuteReader();
        var list = new List<Track>();
        while (r.Read()) list.Add(ReadTrack(r));
        return list;
    });

    public HashSet<string> DownloadIds() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT video_id FROM downloads";
        using var r = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    });

    private const string AllTracksWhere = """
        (p.video_id IS NOT NULL OR t.total_play_ms > 0 OR t.liked_at IS NOT NULL
         OR EXISTS (SELECT 1 FROM playlist_items i WHERE i.video_id = t.video_id)
         OR EXISTS (SELECT 1 FROM downloads d WHERE d.video_id = t.video_id))
        AND NOT EXISTS (SELECT 1 FROM content_blocks b WHERE b.type = 'track' AND b.key = t.video_id)
        """;

    /// <summary>«Все треки» по умолчанию — «Недавно слушали»: последнее прослушивание, у непрослушанных — лайк, остальные в конце.</summary>
    public List<AllTracksEntry> AllTracks() => Database.Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", TrackColumns.Split(", ").Select(x => "t." + x))}, p.last
            FROM tracks t
            LEFT JOIN (SELECT video_id, MAX(played_at) AS last FROM play_events GROUP BY video_id) p ON p.video_id = t.video_id
            WHERE {AllTracksWhere}
            ORDER BY COALESCE(p.last, t.liked_at, 0) DESC
            """;
        using var r = command.ExecuteReader();
        var list = new List<AllTracksEntry>();
        while (r.Read()) list.Add(new AllTracksEntry(ReadTrack(r), r.IsDBNull(14) ? null : r.GetInt64(14), r.GetInt64(13)));
        return list;
    });

    public int AllTracksCount() => Database.Read(c => Convert.ToInt32(LibraryDatabase.Scalar(c, $"""
        SELECT COUNT(*) FROM tracks t
        LEFT JOIN (SELECT DISTINCT video_id FROM play_events) p ON p.video_id = t.video_id
        WHERE {AllTracksWhere}
        """), System.Globalization.CultureInfo.InvariantCulture));

    public (int Likes, int Playlists, int Albums, int Artists) Counts() => Database.Read(c => (
        Convert.ToInt32(LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM tracks WHERE liked_at IS NOT NULL")),
        Convert.ToInt32(LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM playlists")),
        Convert.ToInt32(LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM albums WHERE bookmarked_at IS NOT NULL")),
        Convert.ToInt32(LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM artists WHERE bookmarked_at IS NOT NULL"))));
}
