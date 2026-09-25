using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Melogold.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

/// <summary>Что принёс импорт (docs/spec/backup-format.md §3.4) — для итога.</summary>
public sealed record ImportSummary(int Version, int Tracks, int Plays, int PlaysKnown, int Favorites, int Lyrics, int Playlists, int Saved, int LocalSkipped, int DatesSkipped);

/// <summary>Почему копию не удалось импортировать.</summary>
public enum ImportFailure
{
    NotABackup,
    TooOld,
    Unsupported,
    Unreadable,
}

public sealed class ImportException(ImportFailure reason, Exception? inner = null) : Exception(reason.ToString(), inner)
{
    public ImportFailure Reason { get; } = reason;
}

/// <summary>
/// «Импорт копии» (docs/spec/backup-format.md §3, эталон — Android <c>LegacyImporter</c>): копия ViTune, ViMusic, их
/// форков или Melogold любой платформы <b>добавляется</b> к библиотеке — здесь ничего не заменяется и не удаляется.
/// <list type="bullet">
/// <item>Файл копируется во временную папку и читается там, по колонкам, которые у него есть.</item>
/// <item>Одна транзакция: сбой посередине не меняет ничего.</item>
/// <item>Треки: новые добавляются; у известных — самый ранний лайк, наибольшее время, недостающие поля.</item>
/// <item>Прослушивания: тот же трек в тот же момент — один раз; id — из копии или <see cref="ImportIds"/>, поэтому одна
/// копия на двух устройствах не удваивает историю на сервере.</item>
/// <item>Тексты заполняют только пустые стороны; плейлист находит свой по id сервера, ссылке YouTube или имени.</item>
/// <item>Локальные файлы и невозможные даты не переносятся.</item>
/// </list>
/// Старая копия Windows (своя схема, «Резервное копирование» 0.1.2–0.1.3) сначала переводится в этот формат.
/// </summary>
public static partial class LibraryImport
{
    private const int MinVersion = 12;
    private const long MaxPlayTimeMs = 86_400_000;
    private const int SearchQueries = 200;
    private const int NameMax = 200;

    /// <summary>Даты вне [2000-01-01, 2100-01-01) отбрасываются: сервер их не примет.</summary>
    private const long SaneFrom = 946_684_800_000, SaneUntil = 4_102_444_800_000;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static bool Sane(long at) => at is >= SaneFrom and < SaneUntil;

    /// <summary>Импорт копии <paramref name="path"/> в <paramref name="library"/>; <see cref="ImportException"/> — не вышло.</summary>
    public static ImportSummary Import(Library library, string path, string appVersion = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), "melogold-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "backup.db");
        try
        {
            try
            {
                File.Copy(path, copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ImportException(ImportFailure.Unreadable, e);
            }
            if (!IsSqlite(copy)) throw new ImportException(ImportFailure.NotABackup);

            int version;
            HashSet<string> tables;
            try
            {
                using var connection = Open(copy);
                version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
                tables = Tables(connection);
            }
            catch (SqliteException e)
            {
                throw new ImportException(ImportFailure.Unreadable, e);
            }

            // Старая копия Windows — своя схема: перевести в формат копии и читать его
            if (!tables.Contains("Song") && tables.Contains("tracks") && tables.Contains("play_events"))
            {
                var converted = Path.Combine(directory, "converted.db");
                try
                {
                    LibraryBackup.Export(copy, converted, appVersion);
                }
                catch (SqliteException e)
                {
                    throw new ImportException(ImportFailure.Unreadable, e);
                }
                copy = converted;
                version = LibraryBackup.FormatUserVersion;
                tables = [.. tables, "Song"];
            }
            if (!tables.Contains("Song")) throw new ImportException(tables.Contains("song") ? ImportFailure.Unsupported : ImportFailure.NotABackup);
            if (version is >= 1 and < MinVersion) throw new ImportException(ImportFailure.TooOld);

            Bundle bundle;
            try
            {
                using var connection = Open(copy);
                bundle = new Reader(connection).Read();
            }
            catch (SqliteException e)
            {
                throw new ImportException(ImportFailure.Unreadable, e);
            }
            var summary = library.Database.Write((c, t) => Merge(c, t, bundle, version));
            library.Notify(LibraryChange.All);
            return summary;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        // Своя копия: из WAL в один файл
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE";
        command.ExecuteScalar();
        return connection;
    }

    private static bool IsSqlite(string path)
    {
        var header = new byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 16 && Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3" && header[15] == 0;
    }

    private static HashSet<string> Tables(SqliteConnection c)
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var r = command.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // ---------- Чтение копии ----------

    private sealed record Song(string Id, string Title, string? ArtistsText, string? DurationText, string? ThumbnailUrl, long? LikedAt, long TotalPlayTimeMs, bool Blacklisted, bool Explicit);

    private sealed record Event(string SongId, long Timestamp, long PlayTime, string? SyncId, string? DeviceId);

    private sealed record Lyrics(string SongId, string? Fixed, string? Synced, long? StartTime, string? FixedSource, string? SyncedSource);

    private sealed record Album(string Id, string? Title, string? ThumbnailUrl, string? Year, string? AuthorsText, long? BookmarkedAt);

    private sealed record Artist(string Id, string? Name, string? ThumbnailUrl, long? BookmarkedAt);

    private sealed record Playlist(string Name, string? BrowseId, string? Thumbnail, string? SyncId, List<string> SongIds);

    private sealed record Bundle(
        List<Song> Songs, int LocalSkipped, List<Event> Events, List<Lyrics> Lyrics, List<Album> Albums, List<Artist> Artists,
        List<(string SongId, string AlbumId)> SongAlbums, List<(string SongId, string ArtistId)> SongArtists, List<Playlist> Playlists, List<string> Searches);

    /// <summary>Таблицы копии по колонкам, которые у неё есть; недостающие читаются как NULL.</summary>
    private sealed class Reader(SqliteConnection db)
    {
        private readonly HashSet<string> _tables = Tables(db);
        private readonly Dictionary<string, HashSet<string>> _columns = [];

        private HashSet<string> ColumnsOf(string table)
        {
            if (_columns.TryGetValue(table, out var known)) return known;
            using var command = db.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = command.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read()) set.Add(r.GetString(1));
            return _columns[table] = set;
        }

        private List<T> Select<T>(string table, string[] wanted, Func<Row, T?> read, string? orderBy = null) where T : class
        {
            if (!_tables.Contains(table)) return [];
            var have = ColumnsOf(table);
            var list = string.Join(", ", wanted.Select(w => have.Contains(w) ? $"\"{w}\"" : $"NULL AS \"{w}\""));
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT {list} FROM \"{table}\"{(orderBy is null ? "" : " ORDER BY " + orderBy)}";
            using var r = command.ExecuteReader();
            var row = new Row(r, wanted);
            var result = new List<T>();
            while (r.Read())
                if (read(row) is { } item) result.Add(item);
            return result;
        }

        public Bundle Read()
        {
            var localSkipped = 0;
            var songs = Select("Song", ["id", "title", "artistsText", "durationText", "thumbnailUrl", "likedAt", "totalPlayTimeMs", "blacklisted", "explicit"], row =>
            {
                if (row.String("id") is not { } id) return null;
                if (!VideoId().IsMatch(id))
                {
                    localSkipped++;
                    return null;
                }
                return new Song(id, row.String("title") is { } title && !string.IsNullOrWhiteSpace(title) ? title : id, row.String("artistsText"),
                    row.String("durationText"), row.String("thumbnailUrl"), row.Long("likedAt") is > 0 and var liked ? liked : null,
                    Math.Max(0, row.Long("totalPlayTimeMs") ?? 0), row.Long("blacklisted") == 1, row.Long("explicit") == 1);
            });

            var events = Select("Event", ["songId", "timestamp", "playTime", "syncId", "deviceId"], row =>
                row.String("songId") is { } song && row.Long("timestamp") is { } at
                    ? new Event(song, at, Math.Clamp(row.Long("playTime") ?? 0, 1, MaxPlayTimeMs), row.String("syncId"), row.String("deviceId"))
                    : null, "timestamp");

            var lyrics = Select("Lyrics", ["songId", "fixed", "synced", "startTime", "fixedSource", "syncedSource"], row =>
            {
                if (row.String("songId") is not { } song) return null;
                var fixedText = row.String("fixed") is { Length: > 0 } f ? f : null;
                var synced = row.String("synced") is { Length: > 0 } s ? s : null;
                if (fixedText is null && synced is null) return null;
                return new Lyrics(song, fixedText, synced, row.Long("startTime"),
                    fixedText is null ? null : SourceFromBackup(row.String("fixedSource")), synced is null ? null : SourceFromBackup(row.String("syncedSource")));
            });

            var albums = Select("Album", ["id", "title", "thumbnailUrl", "year", "authorsText", "bookmarkedAt"], row =>
                row.String("id") is { } id ? new Album(id, row.String("title"), row.String("thumbnailUrl"), row.String("year"), row.String("authorsText"), row.Long("bookmarkedAt")) : null);
            var artists = Select("Artist", ["id", "name", "thumbnailUrl", "bookmarkedAt"], row =>
                row.String("id") is { } id ? new Artist(id, row.String("name"), row.String("thumbnailUrl"), row.Long("bookmarkedAt")) : null);
            var songAlbums = Select("SongAlbumMap", ["songId", "albumId"], row =>
                row.String("songId") is { } s && row.String("albumId") is { } a ? Tuple.Create(s, a) : null).Select(x => (x.Item1, x.Item2)).ToList();
            var songArtists = Select("SongArtistMap", ["songId", "artistId"], row =>
                row.String("songId") is { } s && row.String("artistId") is { } a ? Tuple.Create(s, a) : null).Select(x => (x.Item1, x.Item2)).ToList();

            // ViMusic v11 называл связь с плейлистом SongInPlaylist
            var mapTable = _tables.Contains("SongPlaylistMap") ? "SongPlaylistMap" : "SongInPlaylist";
            var items = Select(mapTable, ["songId", "playlistId", "position"], row =>
                row.Long("playlistId") is { } p && row.String("songId") is { } s ? Tuple.Create(p, s) : null, "position, rowid")
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Distinct().ToList());
            var playlists = Select("Playlist", ["id", "name", "browseId", "thumbnail", "syncId"], row =>
                row.Long("id") is { } id
                    ? new Playlist(row.String("name") ?? "", row.String("browseId"), row.String("thumbnail"), row.String("syncId"), items.GetValueOrDefault(id) ?? [])
                    : null, "rowid");

            var searches = Select("SearchQuery", ["query"], row => row.String("query"), "rowid DESC").Take(SearchQueries).ToList();
            return new Bundle(songs, localSkipped, events, lyrics, albums, artists, songAlbums, songArtists, playlists, searches);
        }

        private sealed class Row(SqliteDataReader r, string[] names)
        {
            private readonly Dictionary<string, int> _index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

            public string? String(string name) => r.IsDBNull(_index[name]) ? null : Convert.ToString(r.GetValue(_index[name]), CultureInfo.InvariantCulture);

            public long? Long(string name) => r.IsDBNull(_index[name]) ? null : Convert.ToInt64(r.GetValue(_index[name]), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Источник, как его пишут копии (имена ViTune и Android или слова API §4.10) → слово API.</summary>
    private static string? SourceFromBackup(string? source) => source switch
    {
        "User" or "user" => LyricsSources.User,
        "File" or "file" => LyricsSources.File,
        "YouTubeMusic" or "youtube_music" => LyricsSources.YouTubeMusic,
        "LrcLib" or "lrclib" => LyricsSources.LrcLib,
        "KuGou" or "kugou" => LyricsSources.KuGou,
        _ => null,
    };

    /// <summary>Правило имён плана слияния сервера (API §4.7): NFKC, без краевых пробелов, пробелы схлопнуты, нижний регистр.</summary>
    private static string Norm(string name) => Spaces().Replace(name.Normalize(NormalizationForm.FormKC).Trim(), " ").ToLowerInvariant();

    private static long? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        long total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total * 1000;
    }

    // ---------- Слияние ----------

    private static ImportSummary Merge(SqliteConnection c, SqliteTransaction t, Bundle bundle, int version)
    {
        var now = IsoTime.NowMs();
        int tracks = 0, favorites = 0, datesSkipped = 0;
        var known = new HashSet<string>(StringComparer.Ordinal);
        // Треки, которые библиотека показывает: прослушанные, в Избранном, в плейлистах. ViTune хранит и треки всех
        // открытых альбомов — они приходят (страницы альбомов, тексты), но в итог не входят: их нигде не найти
        var shown = bundle.Events.Select(e => e.SongId).Concat(bundle.Playlists.SelectMany(p => p.SongIds)).ToHashSet(StringComparer.Ordinal);
        var visible = new HashSet<string>(StringComparer.Ordinal);

        foreach (var song in bundle.Songs)
        {
            var likedAt = song.LikedAt is { } l && Sane(l) ? song.LikedAt : null;
            if (song.LikedAt is not null && likedAt is null) datesSkipped++;
            if (shown.Contains(song.Id) || likedAt is not null || song.TotalPlayTimeMs > 0) visible.Add(song.Id);
            var local = Query(c, t, "SELECT title, liked_at, total_play_ms FROM tracks WHERE video_id = $v",
                r => (Title: r.GetString(0), LikedAt: r.IsDBNull(1) ? (long?)null : r.GetInt64(1), Total: r.GetInt64(2)), ("$v", song.Id)).FirstOrDefault();
            if (local.Title is null)
            {
                Exec(c, t, """
                    INSERT INTO tracks (video_id, title, artists_text, duration_ms, duration_text, thumbnail_url, explicit, metadata_stub, liked_at, total_play_ms, created_at)
                    VALUES ($v, $title, $artists, $ms, $text, $thumb, $explicit, $stub, $liked, $total, $now)
                    """,
                    ("$v", song.Id), ("$title", song.Title), ("$artists", song.ArtistsText), ("$ms", ParseDuration(song.DurationText)), ("$text", song.DurationText),
                    ("$thumb", song.ThumbnailUrl), ("$explicit", song.Explicit ? 1 : 0), ("$stub", song.Title == song.Id ? 1 : 0), ("$liked", likedAt),
                    ("$total", song.TotalPlayTimeMs), ("$now", now));
                if (visible.Contains(song.Id)) tracks++;
                if (likedAt is not null) favorites++;
            }
            else
            {
                // Заглушка, названная своим id, узнаёт настоящее название; пустые поля заполняются
                Exec(c, t, """
                    UPDATE tracks SET
                        title = CASE WHEN title = video_id AND $title <> video_id THEN $title ELSE title END,
                        metadata_stub = CASE WHEN title = video_id AND $title <> video_id THEN 0 ELSE metadata_stub END,
                        artists_text = COALESCE(artists_text, $artists),
                        duration_text = COALESCE(duration_text, $text),
                        duration_ms = COALESCE(duration_ms, $ms),
                        thumbnail_url = COALESCE(thumbnail_url, $thumb),
                        liked_at = CASE WHEN liked_at IS NULL THEN $liked WHEN $liked IS NULL THEN liked_at ELSE MIN(liked_at, $liked) END,
                        total_play_ms = MAX(total_play_ms, $total),
                        explicit = MAX(explicit, $explicit)
                    WHERE video_id = $v
                    """,
                    ("$v", song.Id), ("$title", song.Title), ("$artists", song.ArtistsText), ("$text", song.DurationText), ("$ms", ParseDuration(song.DurationText)),
                    ("$thumb", song.ThumbnailUrl), ("$liked", likedAt), ("$total", song.TotalPlayTimeMs), ("$explicit", song.Explicit ? 1 : 0));
                if (local.LikedAt is null && likedAt is not null) favorites++;
            }
            if (song.Blacklisted)
                Exec(c, t, "INSERT OR IGNORE INTO content_blocks (type, key, level, title, subtitle, thumbnail_url, blocked_at) VALUES ('track', $v, 'hide', $title, $sub, $thumb, $now)",
                    ("$v", song.Id), ("$title", song.Title), ("$sub", song.ArtistsText), ("$thumb", song.ThumbnailUrl), ("$now", now));
            known.Add(song.Id);
        }

        bool IsTrack(string id)
        {
            if (known.Contains(id)) return true;
            if (Query(c, t, "SELECT 1 FROM tracks WHERE video_id = $v", r => 1, ("$v", id)).Count == 0) return false;
            known.Add(id);
            return true;
        }

        // Альбомы и исполнители: недостающие добавляются, закладка — самая ранняя
        var saved = 0;
        foreach (var album in bundle.Albums)
        {
            Exec(c, t, "INSERT OR IGNORE INTO albums (browse_id, title, artists_text, year, thumbnail_url) VALUES ($id, $title, $artists, $year, $thumb)",
                ("$id", album.Id), ("$title", album.Title), ("$artists", album.AuthorsText), ("$year", album.Year), ("$thumb", album.ThumbnailUrl));
            if (album.BookmarkedAt is not { } at || !Sane(at)) continue;
            if (Query(c, t, "SELECT bookmarked_at FROM albums WHERE browse_id = $id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", album.Id)).FirstOrDefault() is null) saved++;
            Exec(c, t, "UPDATE albums SET bookmarked_at = MIN(COALESCE(bookmarked_at, $at), $at) WHERE browse_id = $id", ("$at", at), ("$id", album.Id));
        }
        foreach (var artist in bundle.Artists)
        {
            Exec(c, t, "INSERT OR IGNORE INTO artists (browse_id, name, thumbnail_url) VALUES ($id, $name, $thumb)",
                ("$id", artist.Id), ("$name", artist.Name), ("$thumb", artist.ThumbnailUrl));
            if (artist.BookmarkedAt is not { } at || !Sane(at)) continue;
            if (Query(c, t, "SELECT bookmarked_at FROM artists WHERE browse_id = $id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", artist.Id)).FirstOrDefault() is null) saved++;
            Exec(c, t, "UPDATE artists SET bookmarked_at = MIN(COALESCE(bookmarked_at, $at), $at) WHERE browse_id = $id", ("$at", at), ("$id", artist.Id));
        }
        // Трек в альбоме и исполнители трека — только если обе стороны есть; известное здесь не затирается
        var albums = bundle.Albums.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var (songId, albumId) in bundle.SongAlbums)
        {
            if (!albums.TryGetValue(albumId, out var album) || !IsTrack(songId)) continue;
            Exec(c, t, "UPDATE tracks SET album_id = COALESCE(album_id, $a), album_title = COALESCE(album_title, $title) WHERE video_id = $v",
                ("$a", albumId), ("$title", album.Title), ("$v", songId));
        }
        var artists = bundle.Artists.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var group in bundle.SongArtists.Where(m => artists.ContainsKey(m.ArtistId)).GroupBy(m => m.SongId))
        {
            if (!IsTrack(group.Key)) continue;
            var json = JsonSerializer.Serialize(group.Select(m => new { id = m.ArtistId, name = artists[m.ArtistId].Name ?? "" }));
            Exec(c, t, "UPDATE tracks SET artists_json = COALESCE(artists_json, $json) WHERE video_id = $v", ("$json", json), ("$v", group.Key));
        }

        // Прослушивания: тот же трек в тот же момент — один раз, какой бы ни был id
        var playedAt = Query(c, t, "SELECT video_id || ':' || played_at FROM play_events", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
        int plays = 0, playsKnown = 0;
        foreach (var e in bundle.Events)
        {
            if (!IsTrack(e.SongId)) continue;
            if (!Sane(e.Timestamp))
            {
                datesSkipped++;
                continue;
            }
            if (!playedAt.Add($"{e.SongId}:{e.Timestamp}"))
            {
                playsKnown++;
                continue;
            }
            var id = e.SyncId ?? ImportIds.EventId(e.SongId, e.Timestamp, e.PlayTime);
            // С deviceId — прослушивание другого устройства аккаунта: оно уже на сервере
            if (Exec(c, t, "INSERT OR IGNORE INTO play_events (event_id, video_id, played_at, play_time_ms, synced, device_id) VALUES ($id, $v, $at, $ms, $sent, $d)",
                    ("$id", id), ("$v", e.SongId), ("$at", e.Timestamp), ("$ms", e.PlayTime), ("$sent", e.DeviceId is null ? 0 : 1), ("$d", e.DeviceId)) > 0) plays++;
            else playsKnown++;
        }

        // Тексты: только пустые стороны здесь («не искали» или «не нашли»)
        var lyricsAdded = 0;
        foreach (var lyrics in bundle.Lyrics)
        {
            if (!IsTrack(lyrics.SongId)) continue;
            var local = Query(c, t, "SELECT synced, plain FROM lyrics WHERE video_id = $v", r => (Synced: r.IsDBNull(0) ? null : r.GetString(0), Plain: r.IsDBNull(1) ? null : r.GetString(1)), ("$v", lyrics.SongId));
            var offset = -(lyrics.StartTime ?? 0);
            if (local.Count == 0)
            {
                Exec(c, t, "INSERT INTO lyrics (video_id, synced, plain, source, plain_source, offset_ms, fetched_at) VALUES ($v, $s, $p, $ss, $ps, $o, $now)",
                    ("$v", lyrics.SongId), ("$s", lyrics.Synced), ("$p", lyrics.Fixed), ("$ss", lyrics.SyncedSource), ("$ps", lyrics.FixedSource), ("$o", lyrics.Synced is null ? 0 : offset), ("$now", now));
                if (visible.Contains(lyrics.SongId)) lyricsAdded++;
                continue;
            }
            var takePlain = string.IsNullOrEmpty(local[0].Plain) && lyrics.Fixed is not null;
            var takeSynced = string.IsNullOrEmpty(local[0].Synced) && lyrics.Synced is not null;
            if (!takePlain && !takeSynced) continue;
            if (takePlain) Exec(c, t, "UPDATE lyrics SET plain = $p, plain_source = $ps WHERE video_id = $v", ("$p", lyrics.Fixed), ("$ps", lyrics.FixedSource), ("$v", lyrics.SongId));
            if (takeSynced) Exec(c, t, "UPDATE lyrics SET synced = $s, source = $ss, offset_ms = $o WHERE video_id = $v", ("$s", lyrics.Synced), ("$ss", lyrics.SyncedSource), ("$o", offset), ("$v", lyrics.SongId));
            if (visible.Contains(lyrics.SongId)) lyricsAdded++;
        }

        // Плейлисты: тот же (id сервера), иначе с той же ссылкой YouTube или единственный с тем же именем — недостающие
        // треки дописываются в конец; иначе новый
        var playlistsTouched = 0;
        var taken = new HashSet<long>();
        var locals = Query(c, t, "SELECT id, sync_id, browse_id, name FROM playlists ORDER BY id",
            r => (Id: r.GetInt64(0), SyncId: r.IsDBNull(1) ? null : r.GetString(1), BrowseId: r.IsDBNull(2) ? null : r.GetString(2), Name: r.GetString(3)));
        foreach (var imported in bundle.Playlists)
        {
            var tracksOf = imported.SongIds.Where(IsTrack).ToList();
            var free = locals.Where(p => !taken.Contains(p.Id)).ToList();
            var match = free.FirstOrDefault(p => imported.SyncId is not null && p.SyncId == imported.SyncId);
            if (match.Name is null) match = free.FirstOrDefault(p => imported.BrowseId is not null && p.BrowseId == imported.BrowseId);
            if (match.Name is null && free.Where(p => Norm(p.Name) == Norm(imported.Name)).ToList() is [var single]) match = single;
            long playlistId;
            if (match.Name is not null) playlistId = match.Id;
            else
            {
                var name = string.IsNullOrWhiteSpace(imported.Name) ? "—" : Utf16.Truncate(imported.Name, NameMax);
                Exec(c, t, "INSERT INTO playlists (name, browse_id, thumbnail_url, created_at) VALUES ($n, $b, $th, $now)",
                    ("$n", name), ("$b", imported.BrowseId), ("$th", imported.Thumbnail), ("$now", now));
                playlistId = Query(c, t, "SELECT last_insert_rowid()", r => r.GetInt64(0))[0];
            }
            taken.Add(playlistId);
            var have = Query(c, t, "SELECT video_id FROM playlist_items WHERE playlist_id = $p", r => r.GetString(0), ("$p", playlistId)).ToHashSet(StringComparer.Ordinal);
            var start = Query(c, t, "SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_items WHERE playlist_id = $p", r => r.GetInt64(0), ("$p", playlistId))[0];
            var added = tracksOf.Where(id => !have.Contains(id)).ToList();
            for (var i = 0; i < added.Count; i++)
                Exec(c, t, "INSERT INTO playlist_items (playlist_id, video_id, position, added_at) VALUES ($p, $v, $pos, $now)",
                    ("$p", playlistId), ("$v", added[i]), ("$pos", start + i), ("$now", now));
            if (match.Name is null || added.Count > 0) playlistsTouched++;
        }

        // Поиск: последние запросы, новые — сверху
        for (var i = 0; i < bundle.Searches.Count; i++)
            Exec(c, t, "INSERT OR IGNORE INTO search_history (query, searched_at) VALUES ($q, $at)", ("$q", Utf16.Truncate(bundle.Searches[i], 200)), ("$at", now - i));

        // Накопленное время — на сервер заново (play.baseline atLeast) при следующей синхронизации
        Exec(c, t, "INSERT OR REPLACE INTO sync_state (key, value) VALUES ('historyMerge', '1')");

        return new ImportSummary(version, tracks, plays, playsKnown, favorites, lyricsAdded, playlistsTouched, saved, bundle.LocalSkipped, datesSkipped);
    }

    private static int Exec(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] parameters) =>
        LibraryDatabase.Exec(c, t, sql, parameters);

    private static List<T> Query<T>(SqliteConnection c, SqliteTransaction t, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = command.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}
