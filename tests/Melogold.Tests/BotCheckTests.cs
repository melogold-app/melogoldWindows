using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// tasks/0019: YouTube спросил «вы не бот» — ни следующего клиента, ни диагноза, ни повторов; адрес помнится закрытым 10
/// минут, заготовка и загрузки в YouTube не ходят, действие человека — один запрос. YouTube подменён: считаются запросы.
/// </summary>
public sealed class BotCheckTests : IDisposable
{
    private const string BotCheck = """{"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Sign in to confirm you’re not a bot"}}""";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-bot-{Guid.NewGuid():N}");

    public BotCheckTests() => Directory.CreateDirectory(_directory);

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

    private static string Ok(string videoId) =>
        $$$"""{"playabilityStatus":{"status":"OK"},"streamingData":{"adaptiveFormats":[{"itag":140,"url":"https://rr1.googlevideo.com/videoplayback?id={{{videoId}}}&expire=9999999999","mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","bitrate":130000,"contentLength":"1000"}]},"videoDetails":{"lengthSeconds":"200"}}""";

    /// <summary>YouTube: подсказки дают visitorData, <c>player</c> отвечает по <see cref="Player"/>; каждый <c>player</c> записан.</summary>
    private sealed class FakeYouTube : HttpMessageHandler
    {
        private readonly List<string> _players = [];

        public Func<string, string, HttpResponseMessage> Player { get; set; } = (_, _) => Json(BotCheck);

        public IReadOnlyList<string> Players
        {
            get
            {
                lock (_players) return [.. _players];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/get_search_suggestions", StringComparison.Ordinal)) return Json("""{"responseContext":{"visitorData":"CgtWaXNpdG9y"}}""");
            if (!path.EndsWith("/player", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var client = JsonNode.Parse(body)?["context"]?["client"]?["clientName"]?.GetValue<string>() ?? "";
            var videoId = JsonNode.Parse(body)?["videoId"]?.GetValue<string>() ?? "";
            lock (_players) _players.Add($"{request.RequestUri.Host} {client} {videoId}");
            return Player(client, videoId);
        }
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static (FakeYouTube YouTube, StreamResolver Resolver) Setup()
    {
        var youTube = new FakeYouTube();
        var resolver = new StreamResolver(new InnerTubeClient(youTube)) { Clients = [ClientProfile.VisionOs, ClientProfile.AndroidVr] };
        return (youTube, resolver);
    }

    [Fact]
    public async Task BotCheckFromTheFirstClientIsTheOnlyRequest()
    {
        var (youTube, resolver) = Setup();
        var error = await Assert.ThrowsAsync<StreamException>(() => resolver.ResolveAsync("aaaaaaaaaaa", probe: true));
        Assert.Equal(StreamErrorKind.BotCheck, error.Kind);
        Assert.Equal(0, error.Retries);
        // Второй клиент и диагноз (WEB на youtubei.googleapis.com) не спрошены
        Assert.Single(youTube.Players);
        Assert.True(resolver.IsBlocked);
    }

    [Fact]
    public async Task ServerErrorAsksTheNextClient()
    {
        var (youTube, resolver) = Setup();
        youTube.Player = (client, videoId) => client == ClientProfile.VisionOs.Name ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(Ok(videoId));
        var info = await resolver.ResolveAsync("aaaaaaaaaaa", probe: true);
        Assert.Equal(ClientProfile.AndroidVr.Name, info.Source);
        Assert.Equal(2, youTube.Players.Count);
        Assert.False(resolver.IsBlocked);
    }

    [Fact]
    public async Task BlockedAddressIsNotAskedForTenMinutes()
    {
        var (youTube, resolver) = Setup();
        var now = 1_790_000_000_000L;
        resolver.Clock = () => now;
        await Assert.ThrowsAsync<StreamException>(() => resolver.ResolveAsync("aaaaaaaaaaa", probe: true));

        // Заготовка двух следующих треков — ноль запросов
        foreach (var id in new[] { "bbbbbbbbbbb", "ccccccccccc" })
            Assert.Equal(StreamErrorKind.BotCheck, (await Assert.ThrowsAsync<StreamException>(() => resolver.ResolveAsync(id))).Kind);
        Assert.Single(youTube.Players);

        // «Повторить» — ровно один запрос; удача снимает отметку
        var unblocked = 0;
        resolver.Unblocked += () => unblocked++;
        youTube.Player = (_, videoId) => Json(Ok(videoId));
        await resolver.ResolveAsync("aaaaaaaaaaa", probe: true);
        Assert.Equal(2, youTube.Players.Count);
        Assert.False(resolver.IsBlocked);
        Assert.Equal(1, unblocked);

        // Отметка живёт 10 минут; смена сети её снимает
        resolver.MarkBlocked();
        now += 9 * 60_000;
        Assert.True(resolver.IsBlocked);
        now += 2 * 60_000;
        Assert.False(resolver.IsBlocked);
        resolver.MarkBlocked();
        resolver.InvalidateAll();
        Assert.False(resolver.IsBlocked);
    }

    [Fact]
    public async Task GoogleVideo429IsABotCheck()
    {
        var reader = new HttpRangeReader(new HttpClient(new Answer(HttpStatusCode.TooManyRequests)),
            new StreamInfo { VideoId = "aaaaaaaaaaa", Url = "https://rr1.googlevideo.com/videoplayback", Source = "test", ExpiresAtMs = long.MaxValue },
            _ => throw new InvalidOperationException("no refresh"));
        var error = await Assert.ThrowsAsync<StreamException>(() => reader.ReadAsync(0, 1024, TestContext.Current.CancellationToken));
        Assert.Equal(StreamErrorKind.BotCheck, error.Kind);
    }

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task DownloadsWaitAfterOneRequestAndResumeWithOneProbe()
    {
        var (youTube, resolver) = Setup();
        var library = new Library(new LibraryDatabase(Path.Combine(_directory, "library.db")));
        var store = new SongCache(Path.Combine(_directory, "downloads"), () => long.MaxValue, (_, _) => { });
        var player = new SongCache(Path.Combine(_directory, "songs"), () => long.MaxValue, (_, _) => { });
        var downloads = new TrackDownloads(store, player, resolver, library, (_, _) => { });
        var tracks = new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc" }.Select(id => new Track { VideoId = id, Title = id }).ToList();
        foreach (var track in tracks) downloads.Download(track);

        await WaitFor(() => tracks.All(t => downloads.State(t.VideoId)?.Status == DownloadStatus.Waiting), "все три ждут");
        Assert.Single(youTube.Players);
        Assert.True(downloads.IsWaiting);
        // Порядок и список загрузок остаются
        Assert.Equal(tracks.Select(t => t.VideoId).Order(), library.DownloadIds().Order());

        // «Возобновить»: один пробный запрос; не прошёл — снова все ждут
        downloads.ResumeWaiting();
        await WaitFor(() => youTube.Players.Count == 2 && downloads.IsWaiting && tracks.All(t => downloads.State(t.VideoId)?.Status == DownloadStatus.Waiting), "проба и снова ожидание");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, youTube.Players.Count);
        foreach (var track in tracks) downloads.Remove(track.VideoId);
    }

    private sealed class Settings : IPlaybackSettings
    {
        public double Volume => 0;
        public bool Muted => true;
        public double Speed => 1;
        public bool NormalizeVolume => false;
        public bool Autoplay => false;
        public bool PauseHistory => true;
    }

    [Fact]
    public async Task PlayerStopsOnTheCardWithoutSkipping()
    {
        var (youTube, resolver) = Setup();
        var client = new InnerTubeClient(youTube);
        using var engine = new PlayerEngine(resolver, new YouTubeMusic(client), new Library(new LibraryDatabase(Path.Combine(_directory, "player.db"))), new Settings());
        var skipped = 0;
        engine.Skipped += _ => skipped++;
        engine.PlayList(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc" }.Select(id => new Track { VideoId = id, Title = id }).ToList(), 0);
        await WaitFor(() => engine.Status == PlayerStatus.Error, "плеер встал");
        Assert.Equal(StreamErrorKind.BotCheck, engine.Error?.Kind);
        Assert.Equal("aaaaaaaaaaa", engine.Current?.VideoId);
        Assert.Equal(0, skipped);
        Assert.Single(youTube.Players);

        // «Повторить» — ровно один запрос
        engine.Retry();
        await WaitFor(() => youTube.Players.Count == 2 && engine.Status == PlayerStatus.Error, "повтор — один запрос");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, youTube.Players.Count);
        Assert.Equal(0, skipped);
    }
}
