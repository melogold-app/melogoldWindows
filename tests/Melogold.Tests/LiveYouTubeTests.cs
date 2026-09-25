using System.Globalization;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Живые проверки парсеров на настоящих ответах YouTube (как ночной canaryTest Android). Идут только с
/// <c>MELOGOLD_LIVE=1</c>: в CI сеть до YouTube не гарантирована.
/// </summary>
public class LiveYouTubeTests
{
    private static readonly bool Live = Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1";

    private static YouTubeMusic Music()
    {
        var client = new InnerTubeClient();
        (client.Language, client.Region) = InnerTubeClient.LocaleFrom(new CultureInfo("ru-RU"));
        return new YouTubeMusic(client);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchSummaryHasSongsAlbumsArtists()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var summary = await Music().SearchSummaryAsync("кино группа крови", Ct);
        Assert.NotNull(summary.TopResult);
        var songs = summary.Items.OfType<Track>().ToList();
        Assert.Contains(songs, t => t.VideoType == "song" && t.Artists.Any(a => a.Id == "UCL9NQ06h7I0CRUcGxPWMtkQ"));
        Assert.Contains(summary.Items, i => i is AlbumItem { BrowseId: "MPREb_OLmD8O5IYNS" });
        Assert.Contains(summary.Items, i => i is ArtistItem { BrowseId: "UCL9NQ06h7I0CRUcGxPWMtkQ" });
        Assert.All(songs, t => Assert.DoesNotContain(t.ArtistsText ?? "", new[] { "Композиция", "Видео" }));
    }

    [Fact]
    public async Task FilteredSearchPagesContinue()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var music = Music();
        var page = await music.SearchAsync("кино", MusicSearchFilter.Songs, Ct);
        Assert.True(page.Items.OfType<Track>().Count() >= 10);
        Assert.All(page.Items.OfType<Track>(), t => Assert.False(string.IsNullOrEmpty(t.DurationText), t.Title));
        Assert.NotNull(page.Continuation);
        var next = await music.SearchContinuationAsync(page.Continuation!, Ct);
        Assert.NotEmpty(next.Items);

        var albums = await music.SearchAsync("кино", MusicSearchFilter.Albums, Ct);
        Assert.NotEmpty(albums.Items.OfType<AlbumItem>());
        var artists = await music.SearchAsync("кино", MusicSearchFilter.Artists, Ct);
        Assert.NotEmpty(artists.Items.OfType<ArtistItem>());
        var playlists = await music.SearchAsync("кино", MusicSearchFilter.CommunityPlaylists, Ct);
        Assert.NotEmpty(playlists.Items.OfType<PlaylistItem>());
    }

    [Fact]
    public async Task WebSearchVideosAndChannels()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var music = Music();
        var videos = await music.SearchWebAsync("кино группа крови live", WebSearchFilter.Videos, Ct);
        Assert.True(videos.Items.OfType<Track>().Count() >= 5);
        Assert.All(videos.Items.OfType<Track>(), t => Assert.NotNull(t.ArtistsText));
        Assert.NotNull(videos.Continuation);
        var more = await music.SearchWebContinuationAsync(videos.Continuation!, Ct);
        Assert.NotEmpty(more.Items);
        var channels = await music.SearchWebAsync("кино", WebSearchFilter.Channels, Ct);
        Assert.NotEmpty(channels.Items.OfType<ArtistItem>());
    }

    [Fact]
    public async Task ExploreHasTrendingAndMoods()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var shelves = await Music().ExploreAsync(Ct);
        Assert.Contains(shelves, s => s.Items.OfType<Track>().Count() >= 10);
        Assert.Contains(shelves, s => s.Items.OfType<MoodItem>().Count() >= 10);
        Assert.Contains(shelves, s => s.Items.OfType<AlbumItem>().Count() >= 10);
    }

    [Fact]
    public async Task AlbumPlaylistArtist()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var music = Music();
        var album = await music.AlbumAsync("MPREb_OLmD8O5IYNS", Ct);
        Assert.Equal("Группа крови", album.Album.Title);
        Assert.Equal("1988", album.Album.Year);
        Assert.True(album.Tracks.Count >= 10);
        Assert.All(album.Tracks, t => Assert.Equal("Кино", t.ArtistsText));
        Assert.NotNull(album.Album.PlaylistId);

        var tracks = await music.PlaylistTracksAsync("PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", 400, Ct);
        Assert.True(tracks.Count > 150, $"only {tracks.Count}");

        var artist = await music.ArtistAsync("UCL9NQ06h7I0CRUcGxPWMtkQ", Ct);
        Assert.Equal("Кино", artist.Name);
        Assert.Contains(artist.Shelves, s => s.Items.OfType<AlbumItem>().Any());
        Assert.Contains(artist.Shelves, s => s.Items.OfType<Track>().Any());
    }

    [Fact]
    public async Task ChannelWithoutMusicProfile()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var channel = await Music().ChannelAsync("UCk1eGdi3pKvnmdxuzu_Ez6A", Ct);
        Assert.False(string.IsNullOrEmpty(channel.Name));
        Assert.True(channel.Videos.Count >= 10);
    }

    [Fact]
    public async Task RadioLyricsAndStream()
    {
        Assert.SkipUnless(Live, "MELOGOLD_LIVE=1");
        var music = Music();
        var next = await music.NextAsync("xtxjm7ciwmc", "RDAMVMxtxjm7ciwmc", ct: Ct);
        Assert.True(next.Tracks.Count >= 20);
        Assert.NotNull(next.Continuation);
        var more = await music.NextContinuationAsync(next.Continuation!, next.PlaylistId, Ct);
        Assert.NotEmpty(more.Tracks);
        Assert.NotNull(next.LyricsBrowseId);
        var lyrics = await music.LyricsAsync(next.LyricsBrowseId!, Ct);
        Assert.Contains("Группа крови", lyrics!.Value.Text);

        var resolver = new StreamResolver(music.Client);
        var stream = await resolver.ResolveAsync("xtxjm7ciwmc", Ct);
        Assert.Equal(140, stream.Itag);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);
        if (stream.UserAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", stream.UserAgent);
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
    }
}
