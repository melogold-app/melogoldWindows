using System.Text.Json;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Импорт и экспорт копии (docs/spec/backup-format.md §5, tasks/0004): те же случаи, что Android
/// <c>LegacyImporterTest</c> — копия ViTune v30 сливается один раз, копия Melogold проходит круг, чужой файл — не копия.
/// </summary>
public sealed class LibraryImportTests : IDisposable
{
    private const string Rick = "dQw4w9WgXcQ";
    private const string Other = "a1B2c3D4e5F";
    private const long LikedAt = 1_726_000_000_000;
    private const long PlayedAt = 1_726_000_100_000;
    private const string PlaylistSyncId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string EventSyncId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-import-test-{Guid.NewGuid():N}");

    public LibraryImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private Library NewLibrary() => new(new LibraryDatabase(Path.Combine(_directory, $"library-{Guid.NewGuid():N}.db")));

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(Library library, string sql) => (T)Convert.ChangeType(library.Database.Read(c => LibraryDatabase.Scalar(c, sql))!, typeof(T));

    [Fact]
    public void ImportIdsMatchTheSharedVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "spec", "import-ids.vectors.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var vector in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = vector.GetProperty("input");
            Assert.Equal(vector.GetProperty("expected").GetString(),
                ImportIds.EventId(input.GetProperty("videoId").GetString()!, input.GetProperty("timestampMs").GetInt64(), input.GetProperty("playTimeMs").GetInt64()));
        }
    }

    [Fact]
    public void AViTuneV30BackupMergesOnce()
    {
        var library = NewLibrary();
        var first = LibraryImport.Import(library, ViTune30Backup());
        Assert.Equal(30, first.Version);
        Assert.Equal(2, first.Tracks);
        Assert.Equal(1, first.LocalSkipped);
        Assert.Equal(3, first.Plays);
        Assert.Equal(1, first.DatesSkipped);
        Assert.Equal(1, first.Favorites);
        Assert.Equal(1, first.Lyrics);
        Assert.Equal(1, first.Playlists);
        Assert.Equal(1, first.Saved);

        var rick = library.GetTrack(Rick)!;
        Assert.Equal("Never Gonna Give You Up", rick.Title);
        Assert.Equal(["dQw4w9WgXcQ"], library.Favorites().Select(t => t.VideoId));
        Assert.Equal(600_000, library.MostPlayed(null).First(t => t.Track.VideoId == Rick).PlayTimeMs);
        Assert.Equal(ImportIds.EventId(Rick, PlayedAt, 215_000),
            library.Database.Read(c => LibraryDatabase.Scalar(c, $"SELECT event_id FROM play_events WHERE video_id = '{Rick}' ORDER BY played_at LIMIT 1")));
        Assert.Equal(0L, Scalar<long>(library, "SELECT COUNT(*) FROM play_events WHERE synced = 1 OR device_id IS NOT NULL"));
        Assert.Equal("[00:01.00]Never gonna give you up", library.GetLyrics(Rick)?.Synced);
        Assert.Single(library.SavedAlbums());
        var road = library.Playlists().Single(p => p.Name == "Дорога");
        Assert.Equal([Other, Rick], library.PlaylistTracks(road.Id).Select(t => t.VideoId));
        Assert.Equal("1", library.Database.Read(c => LibraryDatabase.Scalar(c, "SELECT value FROM sync_state WHERE key = 'historyMerge'")));

        var again = LibraryImport.Import(library, ViTune30Backup());
        Assert.Equal(0, again.Tracks);
        Assert.Equal(0, again.Plays);
        Assert.Equal(3, again.PlaysKnown);
        Assert.Equal(0, again.Favorites);
        Assert.Equal(0, again.Playlists);
        Assert.Equal(3L, library.PlayCount());
    }

    [Fact]
    public void ACopyOfMelogoldGoesRound()
    {
        var here = NewLibrary();
        here.SetLiked(new Track { VideoId = Rick, Title = "Never Gonna Give You Up", DurationMs = 213_000 }, true);
        here.SaveLyrics(Rick, new StoredLyrics(null, "Мои слова", null, LyricsSources.User));
        var playlist = here.CreatePlaylist("Дорога", [here.GetTrack(Rick)!]);
        here.Database.Write((c, t) =>
        {
            LibraryDatabase.Exec(c, t, "UPDATE playlists SET sync_id = $s WHERE id = $id", ("$s", PlaylistSyncId), ("$id", playlist));
            LibraryDatabase.Exec(c, t, "INSERT INTO play_events (event_id, video_id, played_at, play_time_ms, synced) VALUES ($e, $v, $at, 215000, 1)",
                ("$e", EventSyncId), ("$v", Rick), ("$at", PlayedAt));
        });

        var copy = Path.Combine(_directory, "Melogold_backup.db");
        LibraryBackup.Export(here.Database, copy, "0.1.4");
        using (var c = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "SELECT value FROM MelogoldBackup WHERE key = 'platform'";
            Assert.Equal("windows", command.ExecuteScalar());
            command.CommandText = "SELECT durationText FROM Song";
            Assert.Equal("3:33", command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(31L, command.ExecuteScalar());
        }
        SqliteConnection.ClearAllPools();

        // Другое устройство: тот же плейлист переименован там, больше ничего
        var there = NewLibrary();
        var theirs = there.CreatePlaylist("Road trip");
        there.Database.Write((c, t) => LibraryDatabase.Exec(c, t, "UPDATE playlists SET sync_id = $s WHERE id = $id", ("$s", PlaylistSyncId), ("$id", theirs)));
        var summary = LibraryImport.Import(there, copy);
        Assert.Equal(1, summary.Tracks);
        Assert.Equal(1, summary.Plays);
        Assert.Equal(LyricsSources.User, there.GetLyrics(Rick)?.PlainSource);
        Assert.Equal([theirs], there.Playlists().Select(p => p.Id));
        Assert.Equal([Rick], there.PlaylistTracks(theirs).Select(t => t.VideoId));
        Assert.Equal(EventSyncId, there.Database.Read(c => LibraryDatabase.Scalar(c, "SELECT event_id FROM play_events")));
    }

    [Fact]
    public void AnOldWindowsCopyIsConverted()
    {
        var old = NewLibrary();
        old.RecordPlay(new Track { VideoId = Rick, Title = "Never Gonna Give You Up" }, 60_000, PlayedAt);
        old.SetLiked(old.GetTrack(Rick)!, true);
        SqliteConnection.ClearAllPools();

        var library = NewLibrary();
        var summary = LibraryImport.Import(library, old.Database.Path);
        Assert.Equal(1, summary.Tracks);
        Assert.Equal(1, summary.Plays);
        Assert.Equal(1, summary.Favorites);
    }

    [Fact]
    public void ForeignFilesAreNotBackups()
    {
        var text = Path.Combine(_directory, "hello.db");
        File.WriteAllText(text, "hello");
        Assert.Equal(ImportFailure.NotABackup, Assert.Throws<ImportException>(() => LibraryImport.Import(NewLibrary(), text)).Reason);

        var innertune = Path.Combine(_directory, "innertune.db");
        Exec(innertune, "CREATE TABLE song (id TEXT PRIMARY KEY); PRAGMA user_version = 20;");
        Assert.Equal(ImportFailure.Unsupported, Assert.Throws<ImportException>(() => LibraryImport.Import(NewLibrary(), innertune)).Reason);

        var ancient = Path.Combine(_directory, "ancient.db");
        Exec(ancient, "CREATE TABLE Song (id TEXT PRIMARY KEY, title TEXT); PRAGMA user_version = 5;");
        Assert.Equal(ImportFailure.TooOld, Assert.Throws<ImportException>(() => LibraryImport.Import(NewLibrary(), ancient)).Reason);
    }

    /// <summary>
    /// Настоящая копия по пути из <c>MELOGOLD_IMPORT_SAMPLE</c> (в репозитории её нет: это чья-то история). Ожидаемые
    /// числа — docs/spec/backup-format.md §5.
    /// </summary>
    [Fact]
    public void ARealBackupWhenGiven()
    {
        var path = Environment.GetEnvironmentVariable("MELOGOLD_IMPORT_SAMPLE");
        Assert.SkipUnless(!string.IsNullOrEmpty(path) && File.Exists(path), "MELOGOLD_IMPORT_SAMPLE");
        var library = NewLibrary();
        var summary = LibraryImport.Import(library, path!);
        Console.WriteLine($"IMPORT SAMPLE: {summary}");
        Assert.Equal(195, summary.Tracks);
        Assert.Equal(16_046, summary.Plays);
        Assert.Equal(21, summary.Favorites);
        Assert.Equal(173, summary.Lyrics);
        Assert.Equal(3, summary.Saved);
        Assert.Equal(16, summary.LocalSkipped);

        // Круг: экспорт и импорт в пустую библиотеку дают те же числа
        var copy = Path.Combine(_directory, "round.db");
        LibraryBackup.Export(library.Database, copy, "test");
        var again = LibraryImport.Import(NewLibrary(), copy);
        Assert.Equal(summary.Plays, again.Plays);
        Assert.Equal(summary.Favorites, again.Favorites);
    }

    private string ViTune30Backup()
    {
        var file = Path.Combine(_directory, $"vitune-{Guid.NewGuid():N}.db");
        Exec(file, $"""
            CREATE TABLE Song (id TEXT NOT NULL PRIMARY KEY, title TEXT NOT NULL, artistsText TEXT, durationText TEXT,
                thumbnailUrl TEXT, likedAt INTEGER, totalPlayTimeMs INTEGER NOT NULL, loudnessBoost REAL,
                blacklisted INTEGER NOT NULL DEFAULT 0, explicit INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE Event (id INTEGER PRIMARY KEY AUTOINCREMENT, songId TEXT NOT NULL, timestamp INTEGER NOT NULL, playTime INTEGER NOT NULL);
            CREATE TABLE Lyrics (songId TEXT NOT NULL PRIMARY KEY, fixed TEXT, synced TEXT, startTime INTEGER);
            CREATE TABLE Album (id TEXT NOT NULL PRIMARY KEY, title TEXT, thumbnailUrl TEXT, year TEXT, authorsText TEXT,
                shareUrl TEXT, timestamp INTEGER, bookmarkedAt INTEGER, description TEXT, otherInfo TEXT);
            CREATE TABLE Artist (id TEXT NOT NULL PRIMARY KEY, name TEXT, thumbnailUrl TEXT, timestamp INTEGER, bookmarkedAt INTEGER);
            CREATE TABLE SongAlbumMap (songId TEXT NOT NULL, albumId TEXT NOT NULL, position INTEGER, PRIMARY KEY (songId, albumId));
            CREATE TABLE SongArtistMap (songId TEXT NOT NULL, artistId TEXT NOT NULL, PRIMARY KEY (songId, artistId));
            CREATE TABLE Playlist (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, browseId TEXT, thumbnail TEXT);
            CREATE TABLE SongPlaylistMap (songId TEXT NOT NULL, playlistId INTEGER NOT NULL, position INTEGER NOT NULL, PRIMARY KEY (songId, playlistId));
            CREATE TABLE SearchQuery (id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT NOT NULL);
            INSERT INTO Song VALUES ('{Rick}', 'Never Gonna Give You Up', 'Rick Astley', '3:33', 'https://i.ytimg.com/vi/{Rick}/hq.jpg', {LikedAt}, 600000, NULL, 0, 0);
            INSERT INTO Song VALUES ('{Other}', '', NULL, NULL, NULL, NULL, 0, NULL, 0, 1);
            INSERT INTO Song VALUES ('local:42', 'My file.mp3', NULL, NULL, NULL, NULL, 90000, NULL, 0, 0);
            INSERT INTO Event (songId, timestamp, playTime) VALUES ('{Rick}', {PlayedAt}, 215000);
            INSERT INTO Event (songId, timestamp, playTime) VALUES ('{Rick}', {PlayedAt + 1_000_000}, 385000);
            INSERT INTO Event (songId, timestamp, playTime) VALUES ('{Other}', {PlayedAt + 2_000_000}, 0);
            INSERT INTO Event (songId, timestamp, playTime) VALUES ('{Other}', 5, 1000);
            INSERT INTO Event (songId, timestamp, playTime) VALUES ('local:42', {PlayedAt}, 90000);
            INSERT INTO Lyrics VALUES ('{Rick}', '', '[00:01.00]Never gonna give you up', NULL);
            INSERT INTO Album (id, title, bookmarkedAt) VALUES ('MPREb_album1', 'Whenever You Need Somebody', {LikedAt});
            INSERT INTO SongAlbumMap VALUES ('{Rick}', 'MPREb_album1', 1);
            INSERT INTO Playlist (name) VALUES ('Дорога');
            INSERT INTO SongPlaylistMap VALUES ('{Other}', 1, 0);
            INSERT INTO SongPlaylistMap VALUES ('{Rick}', 1, 1);
            INSERT INTO SongPlaylistMap VALUES ('local:42', 1, 2);
            INSERT INTO SearchQuery (query) VALUES ('rick astley');
            PRAGMA user_version = 30;
            """);
        return file;
    }
}
