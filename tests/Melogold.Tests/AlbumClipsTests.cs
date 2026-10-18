using System.Text.Json.Nodes;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Клипы в списке альбома заменяются песнями из плейлиста альбома (<see cref="YouTubeMusic.PreferAlbumAudio"/>).
/// 18.10.2026: LEMONHEAD показывал 2:11, а играл клип на 1:25 — текст песни расходился со звуком.
/// Фикстуры — те же, что у Linux-клиента (<c>melogold-innertube/tests/fixtures/album-clips</c>).
/// </summary>
public sealed class AlbumClipsTests
{
    private const string AlbumId = "MPREb_rMqWqQe5N3L";
    private const string PlaylistId = "VLOLAK5uy_np90iV2eD-YiScFePYgA7-i3c9kOYXMpc";

    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "album-clips", name)))!;

    private static AlbumDetails Album() => YouTubeMusic.ParseAlbum(AlbumId, Fixture("call-me-if-you-get-lost.album.en.json"));

    private static List<Track> Audio() => YouTubeMusic.ParsePlaylist(PlaylistId, Fixture("call-me-if-you-get-lost.playlist.en.json")).Tracks.ToList();

    [Fact]
    public void AlbumClipsBecomeTheAlbumSongs()
    {
        var album = Album();
        Assert.Equal(16, album.Tracks.Count);
        Assert.All(album.Tracks, t => Assert.Equal("video", t.VideoType));
        Assert.Equal(PlaylistId[2..], album.Album.PlaylistId);

        var tracks = YouTubeMusic.PreferAlbumAudio(album.Tracks, Audio());

        var lemonhead = tracks.Single(t => t.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal));
        Assert.Equal("QjckqsLmb1o", lemonhead.VideoId);
        Assert.Equal("song", lemonhead.VideoType);
        Assert.Equal(131_000, lemonhead.DurationMs);
        Assert.Equal(AlbumId, lemonhead.AlbumId);
        Assert.All(tracks, t => Assert.Equal("song", t.VideoType));
    }

    /// <summary>Пары нет — строка остаётся клипом: лучше сыграть клип, чем чужую песню.</summary>
    [Fact]
    public void AShuffledOrShortPlaylistMatchesByTitleOnly()
    {
        var audio = Audio();
        audio.Reverse();
        audio.RemoveAt(audio.FindIndex(t => t.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal)));

        var tracks = YouTubeMusic.PreferAlbumAudio(Album().Tracks, audio);

        Assert.Equal("a5JCltuobqQ", tracks.Single(t => t.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal)).VideoId);
        Assert.Equal("song", tracks.Single(t => t.Title == "CORSO").VideoType);
        Assert.Equal(15, tracks.Count(t => t.VideoType == "song"));
    }
}
