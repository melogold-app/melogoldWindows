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

    /// <summary>YouTube Music, каким его видит клиент: альбом, его плейлист и, если нужно, отказ на плейлисте.</summary>
    private sealed class FakeAlbums(bool playlistFails) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var browseId = (string)body["browseId"]!;
            if (browseId.StartsWith("VL", StringComparison.Ordinal) && playlistFails) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            var fixture = browseId.StartsWith("VL", StringComparison.Ordinal) ? "call-me-if-you-get-lost.playlist.en.json" : "call-me-if-you-get-lost.album.en.json";
            return new HttpResponseMessage { Content = new StringContent(Fixture(fixture).ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>Починка медиатеки (tasks/0033): пары «клип → песня» из строк альбома и его плейлиста.</summary>
    [Fact]
    public async Task AlbumTwinsPairEachClipWithItsSong()
    {
        using var client = new InnerTubeClient(new FakeAlbums(playlistFails: false));
        var twins = await new YouTubeMusic(client).AlbumTwinsAsync(AlbumId);

        Assert.Equal(16, twins.Count);
        var lemonhead = twins.Single(t => t.Song.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal));
        Assert.Equal("a5JCltuobqQ", lemonhead.ClipId);
        Assert.Equal("QjckqsLmb1o", lemonhead.Song.VideoId);
        Assert.All(twins, t => Assert.Equal("song", t.Song.VideoType));
    }

    /// <summary>Плейлист не загрузился: альбом открывается с клипами, а починка получает ошибку — не «пар нет», иначе она завершилась бы ни с чем.</summary>
    [Fact]
    public async Task AFailedPlaylistIsAnErrorForTheRepairButNotForTheAlbumPage()
    {
        using var client = new InnerTubeClient(new FakeAlbums(playlistFails: true));
        var music = new YouTubeMusic(client);

        var album = await music.AlbumAsync(AlbumId);
        Assert.All(album.Tracks, t => Assert.Equal("video", t.VideoType));
        await Assert.ThrowsAsync<YouTubeException>(() => music.AlbumTwinsAsync(AlbumId));
    }
}
