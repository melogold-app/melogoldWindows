using System.Text.Json.Nodes;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Клипы в списке альбома заменяются песнями из плейлиста альбома (<see cref="YouTubeMusic.PreferAlbumAudio"/>), а если
/// плейлист их не дал — песнями из поиска (<see cref="YouTubeMusic.SongForClip"/>).
/// 18.10.2026: LEMONHEAD показывал 2:11, а играл клип на 1:25 — текст песни расходился со звуком.
/// Фикстуры — те же, что у Linux-клиента (<c>melogold-innertube/tests/fixtures/album-clips</c>).
/// </summary>
public sealed class AlbumClipsTests
{
    private const string AlbumId = "MPREb_rMqWqQe5N3L";
    private const string PlaylistId = "VLOLAK5uy_np90iV2eD-YiScFePYgA7-i3c9kOYXMpc";
    private const string EstateSale = "MPREb_VpXYEs5HeNs";

    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "album-clips", name)))!;

    private static AlbumDetails Album() => YouTubeMusic.ParseAlbum(AlbumId, Fixture("call-me-if-you-get-lost.album.en.json"));

    private static List<Track> Audio() => YouTubeMusic.ParsePlaylist(PlaylistId, Fixture("call-me-if-you-get-lost.playlist.en.json")).Tracks.ToList();

    private static List<Track> Songs(string name) => YouTubeMusic.ParseSearch(Fixture(name)).Items.OfType<Track>().ToList();

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

    /// <summary>
    /// 20.10.2026: у переиздания «CALL ME IF YOU GET LOST: The Estate Sale» плейлист альбома пуст, и LEMONHEAD снова играл
    /// клип на 1:26 вместо песни на 2:11. Песню находит поиск «Песни»: то же название, исполнитель и длительность строки
    /// альбома.
    /// </summary>
    [Fact]
    public void EstateSaleClipsFindTheirSongsBySearch()
    {
        var album = YouTubeMusic.ParseAlbum(EstateSale, Fixture("estate-sale.album.en.json"));
        Assert.Equal(16, album.Tracks.Count(t => t.VideoType == "video"));
        Assert.NotNull(album.Album.PlaylistId);
        // Плейлист пуст: только microformat
        Assert.Throws<YouTubeException>(() => YouTubeMusic.ParsePlaylist("VL" + album.Album.PlaylistId, Fixture("estate-sale.playlist.en.json")));

        foreach (var (title, file, clipId, songId, duration) in new[]
                 {
                     ("LEMONHEAD (feat. 42 Dugg)", "estate-sale.songs-lemonhead.en.json", "a5JCltuobqQ", "QjckqsLmb1o", 131_000L),
                     ("CORSO", "estate-sale.songs-corso.en.json", "Lqvwm3zAILI", "qz-k3DoMxkw", 147_000L),
                 })
        {
            var clip = album.Tracks.Single(t => t.Title == title);
            // В строке — клип, а длительность — песни
            Assert.Equal((clipId, duration), (clip.VideoId, clip.DurationMs));
            var song = YouTubeMusic.SongForClip(clip, Songs(file));
            Assert.NotNull(song);
            Assert.Equal((songId, duration), (song.VideoId, song.DurationMs));
        }
    }

    /// <summary>Пара строгая: другая длительность, другой исполнитель или строка без длительности — клип остаётся клипом.</summary>
    [Fact]
    public void ASongFromSearchMustMatchTitleArtistAndDuration()
    {
        var album = YouTubeMusic.ParseAlbum(EstateSale, Fixture("estate-sale.album.en.json"));
        var lemonhead = album.Tracks.First(t => t.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal));
        var found = Songs("estate-sale.songs-lemonhead.en.json");

        // Длительность клипа, а не песни
        Assert.Null(YouTubeMusic.SongForClip(lemonhead with { DurationMs = 86_000 }, found));
        Assert.Null(YouTubeMusic.SongForClip(lemonhead with { DurationMs = null }, found));
        // Округление в 1,5 с
        Assert.Equal("QjckqsLmb1o", YouTubeMusic.SongForClip(lemonhead with { DurationMs = 132_500 }, found)?.VideoId);

        var stranger = found.Select(song => song with { Artists = [new ArtistRef("UCother", "Other")], ArtistsText = "Other" }).ToList();
        Assert.Null(YouTubeMusic.SongForClip(lemonhead, stranger));
    }

    /// <summary>
    /// YouTube Music, каким его видит клиент: альбом, его плейлист и поиск «Песни» (по сохранённым выдачам, на остальные
    /// запросы — пустая выдача); если нужно — отказ на плейлисте или на поиске.
    /// </summary>
    private sealed class FakeAlbums(bool playlistFails = false, bool searchFails = false) : HttpMessageHandler
    {
        private int _searches;

        public int Searches => _searches;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if ((string?)body["query"] is { } query)
            {
                Interlocked.Increment(ref _searches);
                if (searchFails) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
                return Json(query switch
                {
                    "Tyler, The Creator LEMONHEAD (feat. 42 Dugg)" => Fixture("estate-sale.songs-lemonhead.en.json"),
                    "Tyler, The Creator CORSO" => Fixture("estate-sale.songs-corso.en.json"),
                    _ => new JsonObject(),
                });
            }
            var browseId = (string)body["browseId"]!;
            if (browseId.StartsWith("VL", StringComparison.Ordinal) && playlistFails) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            return Json(Fixture(browseId switch
            {
                AlbumId => "call-me-if-you-get-lost.album.en.json",
                PlaylistId => "call-me-if-you-get-lost.playlist.en.json",
                EstateSale => "estate-sale.album.en.json",
                _ => "estate-sale.playlist.en.json",
            }));
        }

        private static HttpResponseMessage Json(JsonNode node) =>
            new() { Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }

    /// <summary>Починка медиатеки (tasks/0033): пары «клип → песня» из строк альбома и его плейлиста; поиск не нужен.</summary>
    [Fact]
    public async Task AlbumTwinsPairEachClipWithItsSong()
    {
        var youtube = new FakeAlbums();
        using var client = new InnerTubeClient(youtube);
        var twins = await new YouTubeMusic(client).AlbumTwinsAsync(AlbumId, TestContext.Current.CancellationToken);

        Assert.Equal(16, twins.Count);
        var lemonhead = twins.Single(t => t.Song.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal));
        Assert.Equal("a5JCltuobqQ", lemonhead.ClipId);
        Assert.Equal("QjckqsLmb1o", lemonhead.Song.VideoId);
        Assert.All(twins, t => Assert.Equal("song", t.Song.VideoType));
        Assert.Equal(0, youtube.Searches);
    }

    /// <summary>
    /// Плейлист Estate Sale пуст: каждый из 16 клипов ищется поиском, пары нашлись у LEMONHEAD и CORSO (у остальных выдача
    /// пустая — они остаются клипами). Для починки это не ошибка: пустой плейлист не должен повторять её на каждом запуске.
    /// </summary>
    [Fact]
    public async Task AnEmptyPlaylistLeavesTheClipsToTheSearch()
    {
        var youtube = new FakeAlbums();
        using var client = new InnerTubeClient(youtube);
        var music = new YouTubeMusic(client);

        var album = await music.AlbumAsync(EstateSale, TestContext.Current.CancellationToken);
        Assert.Equal(16, youtube.Searches);
        var lemonhead = album.Tracks.Single(t => t.Title.StartsWith("LEMONHEAD", StringComparison.Ordinal));
        Assert.Equal(("QjckqsLmb1o", "song", 131_000L), (lemonhead.VideoId, lemonhead.VideoType, lemonhead.DurationMs));
        // Альбом и название — строки, а не песни из альбома 2021 года
        Assert.Equal((EstateSale, "LEMONHEAD (feat. 42 Dugg)"), (lemonhead.AlbumId, lemonhead.Title));
        Assert.Equal("qz-k3DoMxkw", album.Tracks.Single(t => t.Title == "CORSO").VideoId);
        Assert.Equal(14, album.Tracks.Count(t => t.VideoType == "video"));

        var twins = await music.AlbumTwinsAsync(EstateSale, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { ("Lqvwm3zAILI", "qz-k3DoMxkw"), ("a5JCltuobqQ", "QjckqsLmb1o") }, twins.Select(t => (t.ClipId, t.Song.VideoId)));
    }

    /// <summary>
    /// Плейлист и поиск не загрузились: альбом открывается с клипами, а починка получает ошибку — не «пар нет», иначе она
    /// завершилась бы ни с чем. Не загрузился только плейлист — не ошибка: песни нашёл поиск, его итог сменяет ошибку
    /// плейлиста.
    /// </summary>
    [Fact]
    public async Task AFailedSearchIsAnErrorForTheRepairButNotForTheAlbumPage()
    {
        using (var client = new InnerTubeClient(new FakeAlbums(playlistFails: true, searchFails: true)))
        {
            var music = new YouTubeMusic(client);
            var album = await music.AlbumAsync(AlbumId, TestContext.Current.CancellationToken);
            Assert.All(album.Tracks, t => Assert.Equal("video", t.VideoType));
            await Assert.ThrowsAsync<YouTubeException>(() => music.AlbumTwinsAsync(AlbumId, TestContext.Current.CancellationToken));
        }
        using (var client = new InnerTubeClient(new FakeAlbums(playlistFails: true)))
        {
            var twins = await new YouTubeMusic(client).AlbumTwinsAsync(AlbumId, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { ("Lqvwm3zAILI", "qz-k3DoMxkw"), ("a5JCltuobqQ", "QjckqsLmb1o") }, twins.Select(t => (t.ClipId, t.Song.VideoId)));
        }
    }
}
