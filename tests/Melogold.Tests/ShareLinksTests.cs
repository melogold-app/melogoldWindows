using System.Net;
using System.Text;
using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0016: ссылки «Поделиться», ссылки на снимки Melogold, ссылки других сервисов и заголовки их страниц.</summary>
public sealed class ShareLinksTests
{
    [Fact]
    public void LinksOfYouTube()
    {
        Assert.Equal("https://music.youtube.com/watch?v=dQw4w9WgXcQ", ShareLinks.Track("dQw4w9WgXcQ", isMusic: true));
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", ShareLinks.Track("dQw4w9WgXcQ", isMusic: false));
        Assert.Equal("https://music.youtube.com/browse/MPREb_abc", ShareLinks.Album("MPREb_abc"));
        Assert.Equal("https://music.youtube.com/channel/UCabc", ShareLinks.Artist("UCabc", channel: false));
        Assert.Equal("https://www.youtube.com/channel/UCabc", ShareLinks.Artist("UCabc", channel: true));
        Assert.Equal("https://music.youtube.com/playlist?list=PLabc", ShareLinks.Playlist("VLPLabc"));
        Assert.Equal("https://music.youtube.com/playlist?list=PLabc", ShareLinks.Playlist("PLabc"));
    }

    [Fact]
    public void WatchVideosTakesTheFirstFiftyVideos()
    {
        var ids = Enumerable.Range(0, 60).Select(i => $"video{i:000000}").Prepend("not a video").ToList();
        var url = ShareLinks.WatchVideos(ids);
        Assert.NotNull(url);
        Assert.StartsWith("https://www.youtube.com/watch_videos?video_ids=video000000,video000001,", url);
        Assert.Equal(50, url![(url.IndexOf('=') + 1)..].Split(',').Length);
        Assert.Null(ShareLinks.WatchVideos(["local:1"]));
    }

    [Fact]
    public void MessageIsTitleArtistAndLink()
    {
        Assert.Equal("Песня — Кино\nhttps://x", ShareLinks.Message(" Песня ", "Кино", "https://x"));
        Assert.Equal("Песня\nhttps://x", ShareLinks.Message("Песня", " ", "https://x"));
        Assert.Equal("https://x", ShareLinks.Message("", null, "https://x"));
        Assert.Equal("melogold://share?v=1&url=https%3A%2F%2Fmusic.example.com&id=a1B2c3D4e5", ShareLinks.MelogoldShare("https://music.example.com", "a1B2c3D4e5"));
    }

    [Fact]
    public void ShareLinksOfMelogold()
    {
        Assert.Equal(new ShareRef("https://music.example.com", "a1B2c3D4e5"),
            ShareLinkParser.Parse("melogold://share?v=1&url=https%3A%2F%2Fmusic.example.com&id=a1B2c3D4e5"));
        Assert.Equal(new ShareRef("https://music.example.com/melogold", "a1B2c3D4e5"),
            ShareLinkParser.Parse("Держи: https://music.example.com/melogold/s/a1B2c3D4e5."));
        Assert.Equal(new ShareRef("http://192.168.1.50:8080", "a1B2c3D4e5"), ShareLinkParser.Parse("http://192.168.1.50:8080/s/a1B2c3D4e5"));
        // Неверная версия, id, адрес: не снимок
        Assert.Null(ShareLinkParser.Parse("melogold://share?v=2&url=https%3A%2F%2Fmusic.example.com&id=a1B2c3D4e5"));
        Assert.Null(ShareLinkParser.Parse("melogold://share?v=1&url=https%3A%2F%2Fmusic.example.com&id=short"));
        Assert.Null(ShareLinkParser.Parse("melogold://server?v=1&url=https%3A%2F%2Fmusic.example.com"));
        Assert.Null(ShareLinkParser.Parse("http://music.example.com/s/a1B2c3D4e5"));
        Assert.Null(ShareLinkParser.Parse("https://music.youtube.com/watch?v=dQw4w9WgXcQ"));
        Assert.Null(ShareLinkParser.Parse("https://music.example.com/s/a1B2c3D4e5/more"));
    }

    [Theory]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=abc123", MusicService.Spotify, MusicLinkKind.Track)]
    [InlineData("https://open.spotify.com/intl-de/track/4cOdK2wGLETKBW3PvgPWqT", MusicService.Spotify, MusicLinkKind.Track)]
    [InlineData("https://open.spotify.com/album/6eUW0wxWtzkFdaEFsTJto6", MusicService.Spotify, MusicLinkKind.Album)]
    [InlineData("https://open.spotify.com/artist/0gxyHStUsqpMadRV0Di1Qt", MusicService.Spotify, MusicLinkKind.Artist)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", MusicService.Spotify, MusicLinkKind.Playlist)]
    [InlineData("https://spotify.link/abc123XYZ", MusicService.Spotify, MusicLinkKind.Unknown)]
    [InlineData("https://music.apple.com/us/song/never-gonna-give-you-up/1559523359", MusicService.AppleMusic, MusicLinkKind.Track)]
    [InlineData("https://music.apple.com/us/album/whenever-you-need-somebody/1559523357", MusicService.AppleMusic, MusicLinkKind.Album)]
    [InlineData("https://music.apple.com/ru/album/whenever-you-need-somebody/1559523357?i=1559523359", MusicService.AppleMusic, MusicLinkKind.Track)]
    [InlineData("https://music.apple.com/us/artist/rick-astley/669771", MusicService.AppleMusic, MusicLinkKind.Artist)]
    [InlineData("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", MusicService.AppleMusic, MusicLinkKind.Playlist)]
    [InlineData("https://music.yandex.ru/album/1179999/track/609676", MusicService.YandexMusic, MusicLinkKind.Track)]
    [InlineData("https://music.yandex.com/track/609676", MusicService.YandexMusic, MusicLinkKind.Track)]
    [InlineData("https://music.yandex.ru/album/1179999", MusicService.YandexMusic, MusicLinkKind.Album)]
    [InlineData("https://music.yandex.by/artist/36800", MusicService.YandexMusic, MusicLinkKind.Artist)]
    [InlineData("https://music.yandex.ru/users/yamusic-daily/playlists/1000", MusicService.YandexMusic, MusicLinkKind.Playlist)]
    [InlineData("https://www.deezer.com/en/track/3135556", MusicService.Deezer, MusicLinkKind.Track)]
    [InlineData("https://www.deezer.com/ru/album/302127", MusicService.Deezer, MusicLinkKind.Album)]
    [InlineData("https://link.deezer.com/s/32abc", MusicService.Deezer, MusicLinkKind.Unknown)]
    [InlineData("https://tidal.com/browse/track/491206012", MusicService.Tidal, MusicLinkKind.Track)]
    [InlineData("https://listen.tidal.com/album/491206010", MusicService.Tidal, MusicLinkKind.Album)]
    [InlineData("https://soundcloud.com/rick-astley-official/never-gonna-give-you-up-4", MusicService.SoundCloud, MusicLinkKind.Track)]
    [InlineData("https://soundcloud.com/user/sets/my-set", MusicService.SoundCloud, MusicLinkKind.Playlist)]
    [InlineData("https://soundcloud.com/rick-astley-official", MusicService.SoundCloud, MusicLinkKind.Artist)]
    public void LinksOfOtherServices(string url, MusicService service, MusicLinkKind kind)
    {
        var link = MusicServiceLinkParser.Parse(url);
        Assert.Equal((service, kind), (link?.Service, link?.Kind));
    }

    [Fact]
    public void LinkInTextAndNotALink()
    {
        Assert.Equal("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", MusicServiceLinkParser.Parse("Слушай, это огонь: https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT.")?.Url);
        Assert.Null(MusicServiceLinkParser.Parse("https://open.spotify.com/show/abc"));
        Assert.Null(MusicServiceLinkParser.Parse("https://music.youtube.com/watch?v=dQw4w9WgXcQ"));
        Assert.Null(MusicServiceLinkParser.Parse("https://example.com/track/1"));
        Assert.Null(MusicServiceLinkParser.Parse("open.spotify.com/track/abc"));
        Assert.Null(MusicServiceLinkParser.Parse(""));
    }

    private static string? Query(MusicService service, MusicLinkKind kind, string page) =>
        PageTitles.SearchText(new MusicServiceLink(service, kind, "u"), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages", page + ".html")));

    [Fact]
    public void PageTitlesOfEachService()
    {
        Assert.Equal("Never Gonna Give You Up Rick Astley", Query(MusicService.Spotify, MusicLinkKind.Track, "spotify-track"));
        Assert.Equal("Whenever You Need Somebody Rick Astley", Query(MusicService.Spotify, MusicLinkKind.Album, "spotify-album"));
        Assert.Equal("Rick Astley", Query(MusicService.Spotify, MusicLinkKind.Artist, "spotify-artist"));
        Assert.Equal("Never Gonna Give You Up Rick Astley", Query(MusicService.AppleMusic, MusicLinkKind.Track, "apple-track"));
        Assert.Equal("3 Originals Rick Astley", Query(MusicService.AppleMusic, MusicLinkKind.Album, "apple-album"));
        Assert.Equal("Never Gonna Give You Up Rick Astley", Query(MusicService.YandexMusic, MusicLinkKind.Track, "yandex-track"));
        Assert.Equal("De Verdade Bokaloka", Query(MusicService.YandexMusic, MusicLinkKind.Album, "yandex-album"));
        Assert.Equal("Never Gonna Give You Up Rick Astley", Query(MusicService.Tidal, MusicLinkKind.Track, "tidal-track"));
        Assert.Equal("Daft Punk Harder, Better, Faster, Stronger", Query(MusicService.Deezer, MusicLinkKind.Track, "deezer-track"));
        Assert.Null(Query(MusicService.SoundCloud, MusicLinkKind.Track, "soundcloud-track"));
    }

    [Fact]
    public void EntitiesAttributeOrderAndLongText()
    {
        var link = new MusicServiceLink(MusicService.Spotify, MusicLinkKind.Track, "u");
        const string html = """<html><head><TITLE>Tom &amp; Jerry &#8211; Theme - song and lyrics by A&#x27;B | Spotify</TITLE><meta content="x" property="og:title"></head></html>""";
        Assert.Equal("Tom & Jerry – Theme A'B", PageTitles.SearchText(link, html));
        Assert.Equal(120, PageTitles.SearchText(link, "<title>" + new string('a', 300) + " - song by B | Spotify</title>")?.Length);
        Assert.Null(PageTitles.SearchText(link with { Kind = MusicLinkKind.Artist }, "<html></html>"));
    }

    private sealed class FakePages(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Page(string html, string landedAt) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html"),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, landedAt),
    };

    [Fact]
    public async Task ExternalLinkIsSearchedByItsPageTitle()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages", "spotify-track.html"));
        var handler = new FakePages(request => Page(html, request.RequestUri!.AbsoluteUri));
        var resolver = new ExternalLinkResolver(new HttpClient(handler));
        var link = MusicServiceLinkParser.Parse("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT")!;
        Assert.Equal(new ExternalResolution.Search("Never Gonna Give You Up Rick Astley"), await resolver.ResolveAsync(link));
        // Второй раз — из памяти; плейлист — без сети
        await resolver.ResolveAsync(link);
        Assert.IsType<ExternalResolution.Playlist>(await resolver.ResolveAsync(MusicServiceLinkParser.Parse("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")!));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ShortLinkIsKnownByWhereItLands()
    {
        var handler = new FakePages(_ => Page("<title>Today's Top Hits | Spotify Playlist</title>", "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"));
        var resolver = new ExternalLinkResolver(new HttpClient(handler));
        Assert.IsType<ExternalResolution.Playlist>(await resolver.ResolveAsync(MusicServiceLinkParser.Parse("https://spotify.link/abc123XYZ")!));
    }

    [Fact]
    public async Task NoNetworkIsNotRemembered()
    {
        var handler = new FakePages(_ => throw new HttpRequestException("offline"));
        var resolver = new ExternalLinkResolver(new HttpClient(handler));
        var link = MusicServiceLinkParser.Parse("https://music.yandex.ru/album/1179999/track/609676")!;
        Assert.IsType<ExternalResolution.Offline>(await resolver.ResolveAsync(link));
        Assert.IsType<ExternalResolution.Offline>(await resolver.ResolveAsync(link));
        Assert.Equal(2, handler.Calls);
    }
}
