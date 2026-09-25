using Melogold.Core.Data;
using Melogold.Core.Music;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Melogold.Tests;

/// <summary>«Все треки» (tasks/0005 §4, как Android <c>AllTracksTest</c>): состав, порядок и число.</summary>
public sealed class AllTracksTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"melogold-all-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(file);
    }

    private static Track T(string id, string title) => new() { VideoId = id, Title = title, ArtistsText = "Melogold test" };

    [Fact]
    public void PlayedLikedAndPlaylistTracksInRecentOrder()
    {
        var library = new Library(new LibraryDatabase(_path));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        library.RecordPlay(T("aaaaaaaaaaa", "Long ago"), 60_000, now - 400L * 24 * 3600 * 1000);
        library.RecordPlay(T("bbbbbbbbbbb", "Today"), 120_000, now - 60_000);
        library.SetLiked(T("ccccccccccc", "Liked"), true);
        library.CreatePlaylist("Дорога", [T("ddddddddddd", "In a playlist")]);
        library.RecordPlay(T("eeeeeeeeeee", "Hidden"), 60_000, now);
        library.SetTrackHidden(library.GetTrack("eeeeeeeeeee")!, true);
        // Трек альбома, который только открывали: есть в tracks, но не слушали, не лайкали, не в плейлистах
        library.Database.Write((c, t) => LibraryDatabase.Exec(c, t,
            "INSERT INTO tracks (video_id, title, created_at) VALUES ('fffffffffff', 'Only from an album', 0)"));

        var all = library.AllTracks();
        // По последнему прослушиванию, у непрослушанного — по лайку (лайк сейчас позже прослушивания минуту назад),
        // остальные — в конце
        Assert.Equal(["ccccccccccc", "bbbbbbbbbbb", "aaaaaaaaaaa", "ddddddddddd"], all.Select(e => e.Track.VideoId));
        Assert.Null(all[0].LastPlayedAt);
        Assert.Equal(120_000, all[1].PlayTimeMs);
        Assert.NotNull(all[1].LastPlayedAt);
        Assert.Equal(4, library.AllTracksCount());
    }
}
