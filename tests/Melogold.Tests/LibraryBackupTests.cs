using Melogold.Core.Data;
using Melogold.Core.Music;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Melogold.Tests;

/// <summary>Резервная копия базы: библиотека и свои тексты в ней, найденные в сети тексты — нет; чужой файл не восстановить.</summary>
public sealed class LibraryBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-backup-{Guid.NewGuid():N}");

    public LibraryBackupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void BackupKeepsLibraryWithoutCache()
    {
        var database = new LibraryDatabase(Path.Combine(_directory, "library.db"));
        var library = new Library(database);
        var track = new Track { VideoId = "dQw4w9WgXcQ", Title = "Never Gonna Give You Up", ArtistsText = "Rick Astley" };
        library.SetLiked(track, true);
        library.CreatePlaylist("Дорога", [track]);
        library.SaveLyrics("dQw4w9WgXcQ", new StoredLyrics("[00:01.00]Свой", "", LyricsSources.File, null));
        library.SaveLyrics("kJQP7kiw5Fk", new StoredLyrics("[00:01.00]Из сети", "", LyricsSources.LrcLib, null));

        var backup = Path.Combine(_directory, "Melogold_backup.db");
        LibraryBackup.Export(database, backup);
        Assert.True(LibraryBackup.IsBackup(backup));

        var restored = new Library(new LibraryDatabase(backup));
        Assert.Equal(["dQw4w9WgXcQ"], restored.LikedIds());
        Assert.Single(restored.Playlists(), p => p.Name == "Дорога");
        Assert.Equal(LyricsSources.File, restored.GetLyrics("dQw4w9WgXcQ")?.SyncedSource);
        Assert.Null(restored.GetLyrics("kJQP7kiw5Fk"));
    }

    [Fact]
    public void ForeignFilesAreNotBackups()
    {
        var text = Path.Combine(_directory, "notes.db");
        File.WriteAllText(text, "не база");
        Assert.False(LibraryBackup.IsBackup(text));

        // Другая SQLite-база (например, Android) и база новее этой версии
        var other = Path.Combine(_directory, "other.db");
        using (var connection = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Song (id TEXT); PRAGMA user_version = 30;";
            command.ExecuteNonQuery();
        }
        Assert.False(LibraryBackup.IsBackup(other));

        var newer = Path.Combine(_directory, "newer.db");
        _ = new LibraryDatabase(newer);
        using (var connection = new SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {LibraryDatabase.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }
        Assert.False(LibraryBackup.IsBackup(newer));
    }
}
