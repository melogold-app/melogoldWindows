using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// tasks/0033: сохранённые клипы из альбомов — на песни. Сценарий Linux-теста <c>a_saved_clip_becomes_its_song</c>
/// (melogoldLinux, задание 0030) и запуск починки один раз.
/// </summary>
public sealed class AlbumClipsRepairTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-clips-{Guid.NewGuid():N}");

    public AlbumClipsRepairTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    private Library NewLibrary() => new(new LibraryDatabase(Path.Combine(_directory, $"library-{Guid.NewGuid():N}.db")));

    private static Track Plain(string id) => new() { VideoId = id, Title = $"Трек {id}", ArtistsText = "Кино" };

    private static readonly Track Clip = new()
    {
        VideoId = "clip",
        Title = "LEMONHEAD (feat. 42 Dugg)",
        AlbumId = "MPREb_rMqWqQe5N3L",
        VideoType = "video",
    };

    private static readonly Track Song = Clip with { VideoId = "song", VideoType = "song" };

    private static long LikedAt(Library library, string id) => library.Database.Read(c =>
        Convert.ToInt64(LibraryDatabase.Scalar(c, "SELECT liked_at FROM tracks WHERE video_id = $id", ("$id", id))));

    private static List<string> Ids(Library library, long playlistId) => library.PlaylistTracks(playlistId).Select(t => t.VideoId).ToList();

    /// <summary>♡ и места в плейлистах переходят с клипа на песню; там, где песня уже есть, клип просто убирается; свои названия — тоже на песню.</summary>
    [Fact]
    public void ASavedClipBecomesItsSong()
    {
        var library = NewLibrary();
        library.SetLiked(Clip, true);
        var likedAt = LikedAt(library, "clip");
        var mixed = library.CreatePlaylist("Смесь", [Plain("a"), Clip, Plain("b")]);
        var both = library.CreatePlaylist("Обе", [Song, Clip]);
        library.SetOverride(Clip, TrackOverride.Of("Лимонная голова", null, null));
        library.SetLiked(Plain("x"), true);
        library.UpdatePin("clip", new Melogold.Core.Lyrics.LyricsPin("lrclib", "42", null));

        Assert.Equal(["clip"], library.SavedAlbumClips().Select(t => t.VideoId));
        var replaced = library.ReplaceClipWithSong("clip", Song);
        Assert.Equal(new ClipReplaced(true, 2, true, true), replaced);

        Assert.False(library.IsLiked("clip"));
        Assert.True(library.IsLiked("song"));
        Assert.Equal(likedAt, LikedAt(library, "song"));
        Assert.Equal(["a", "song", "b"], Ids(library, mixed));
        Assert.Equal(["song"], Ids(library, both));
        Assert.Equal("Лимонная голова", library.Display(Song).Title);
        Assert.Equal("LEMONHEAD (feat. 42 Dugg)", library.Display(Clip).Title);
        Assert.Equal("42", library.LyricsPinOf("song")?.Ref);
        Assert.Null(library.LyricsPinOf("clip"));
        Assert.Empty(library.SavedAlbumClips());
        // Песня сохранена как песня, клип остался в tracks (загрузки и история — за ним)
        Assert.Equal("song", library.GetTrack("song")!.VideoType);
        Assert.NotNull(library.GetTrack("clip"));
    }

    /// <summary>Своё название песни не затирается названием клипа; позиции в плейлисте перенумерованы без дыр.</summary>
    [Fact]
    public void TheSongsOwnOverrideAndPlaylistOrderSurvive()
    {
        var library = NewLibrary();
        var playlist = library.CreatePlaylist("Список", [Clip, Plain("a"), Song, Plain("b")]);
        library.SetOverride(Clip, TrackOverride.Of("Название клипа", null, null));
        library.SetOverride(Song, TrackOverride.Of("Название песни", null, null));

        var replaced = library.ReplaceClipWithSong("clip", Song);

        Assert.False(replaced.MovedOverride);
        Assert.Equal("Название песни", library.Display(Song).Title);
        Assert.Equal(["a", "song", "b"], Ids(library, playlist));
        var positions = library.Database.Read(c => LibraryDatabase.Scalar(c, "SELECT group_concat(position) FROM (SELECT position FROM playlist_items WHERE playlist_id = $p ORDER BY position)", ("$p", playlist)));
        Assert.Equal("0,1,2", positions);
    }

    /// <summary>
    /// Синхронизация строит ops разницей библиотеки со снимком сервера (<c>synced_*</c>): после замены она видит снятый ♡ у
    /// клипа, поставленный у песни (с прежней датой) и другой состав плейлиста — ровно то, что от ручных правок.
    /// </summary>
    [Fact]
    public void SyncSeesTheReplacementAsOrdinaryEdits()
    {
        var library = NewLibrary();
        var store = new SyncStore(library);
        library.SetLiked(Clip, true);
        var likedAt = LikedAt(library, "clip");
        var playlist = library.CreatePlaylist("Смесь", [Plain("a"), Clip]);
        store.Run(tx =>
        {
            tx.SetLike("clip", likedAt);
            tx.SetPlaylistSyncId(playlist, "pl-1");
            tx.UpsertSyncedPlaylist(new SyncedPlaylist("pl-1", "Смесь", null, ["a", "clip"]));
            tx.UpsertItem(playlist, "a", "k1", 1);
            tx.UpsertItem(playlist, "clip", "k2", 1);
        });

        library.ReplaceClipWithSong("clip", Song);

        store.Run(tx =>
        {
            var likes = tx.Likes();
            Assert.Equal(["song"], likes.Select(l => l.Track.VideoId));
            Assert.Equal(likedAt, likes[0].LikedAt);
            Assert.Equal(["clip"], tx.SyncedLikes());
            Assert.Equal(["a", "song"], tx.PlaylistVideoIds(playlist));
            Assert.Equal(["a", "clip"], tx.SyncedPlaylists()["pl-1"].VideoIds);
        });
        // Песню сервер ещё не знает: ключ места клипа за ней не остаётся
        Assert.Equal("a", library.Database.Read(c => LibraryDatabase.Scalar(c, "SELECT group_concat(video_id) FROM playlist_items WHERE sort_key IS NOT NULL")));
    }

    /// <summary>Замену видят экраны: уведомление о ♡, плейлистах, правках и текстах.</summary>
    [Fact]
    public void ScreensAreNotified()
    {
        var library = NewLibrary();
        library.SetLiked(Clip, true);
        LibraryChange seen = LibraryChange.None;
        library.Changed += change => seen |= change;

        library.ReplaceClipWithSong("clip", Song);

        Assert.True(seen.HasFlag(LibraryChange.Likes | LibraryChange.Playlists | LibraryChange.Overrides | LibraryChange.Lyrics));
    }

    [Fact]
    public async Task TheRepairRunsOnceAndRetriesWithoutNetwork()
    {
        var library = NewLibrary();
        library.SetLiked(Clip, true);
        library.SetLiked(Plain("x"), true);
        var lines = new List<string>();
        var offline = true;
        var asked = new List<string>();
        Task<IReadOnlyList<(string ClipId, Track Song)>> Twins(string album, CancellationToken ct)
        {
            asked.Add(album);
            if (offline) throw new HttpRequestException("нет сети");
            return Task.FromResult<IReadOnlyList<(string ClipId, Track Song)>>([("clip", Song), ("other", Plain("zzz"))]);
        }

        var first = await AlbumClipsRepair.RunAsync(library, Twins, TimeSpan.Zero, (message, _) => lines.Add(message));

        Assert.Equal(new AlbumClipsRepair.Result(1, 0, true), first);
        Assert.Null(library.GetState(AlbumClipsRepair.DoneKey));
        Assert.True(library.IsLiked("clip"));

        offline = false;
        var second = await AlbumClipsRepair.RunAsync(library, Twins, TimeSpan.Zero, (message, _) => lines.Add(message));

        Assert.Equal(new AlbumClipsRepair.Result(1, 1, false), second);
        Assert.Equal("1", library.GetState(AlbumClipsRepair.DoneKey));
        Assert.True(library.IsLiked("song") && !library.IsLiked("clip"));
        Assert.Null(library.GetTrack("zzz")); // пара чужого клипа — не из медиатеки — не сохраняется
        Assert.Contains(lines, l => l.StartsWith("Клип в медиатеке заменён песней", StringComparison.Ordinal));

        // Готово — третий запуск ничего не спрашивает
        asked.Clear();
        Assert.Null(await AlbumClipsRepair.RunAsync(library, Twins, TimeSpan.Zero, (message, _) => lines.Add(message)));
        Assert.Empty(asked);
    }
}
