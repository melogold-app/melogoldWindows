using System.Diagnostics;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// tasks/0030: нет сети — трек не пропускается, а ждёт её в буферизации; «сеть появилась» — повтор сразу, не дожидаясь паузы;
/// предел ожидания вышел — ошибка «нет сети», трек и очередь на месте. YouTube подменён: на каждый запрос — обрыв соединения.
/// </summary>
public sealed class NetworkWaitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-net-{Guid.NewGuid():N}");

    public NetworkWaitTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>Сети нет: каждый запрос обрывается, как при недоступном DNS или упавшем туннеле VPN.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new HttpRequestException("No such host is known.");
        }
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

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 15)
    {
        for (var i = 0; i < seconds * 40 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task WithoutNetworkTheTrackWaitsInsteadOfSkipping()
    {
        var network = new NoNetwork();
        var client = new InnerTubeClient(network);
        var resolver = new StreamResolver(client) { Clients = [ClientProfile.VisionOs] };
        using var engine = new PlayerEngine(resolver, new YouTubeMusic(client), new Library(new LibraryDatabase(Path.Combine(_directory, "player.db"))), new Settings())
        {
            NetworkWaitDelays = [TimeSpan.FromSeconds(2)],
            NetworkWaitLimit = TimeSpan.FromSeconds(3),
        };
        var skipped = 0;
        engine.Skipped += _ => skipped++;
        engine.PlayList(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc" }.Select(id => new Track { VideoId = id, Title = id }).ToList(), 0);

        // Отказ после быстрых повторов — буферизация на том же треке, не пропуск
        await WaitFor(() => engine.Status == PlayerStatus.Buffering, "трек ждёт сеть");
        Assert.Equal("aaaaaaaaaaa", engine.Current?.VideoId);
        Assert.Equal(0, skipped);

        // Сеть появилась — повтор сразу, раньше паузы в 2 с
        var calls = network.Calls;
        var asked = Stopwatch.StartNew();
        engine.NotifyNetworkAvailable();
        await WaitFor(() => network.Calls > calls, "по сигналу сети YouTube спрошен снова", seconds: 2);
        Assert.True(asked.Elapsed < TimeSpan.FromSeconds(1.5), $"повтор не ждал паузы: {asked.Elapsed}");

        // Предел ожидания — ошибка «нет сети», трек и очередь на месте, ничего не пропущено
        await WaitFor(() => engine.Status == PlayerStatus.Error, "плеер встал с ошибкой", seconds: 30);
        Assert.Equal(StreamErrorKind.Network, engine.Error?.Kind);
        Assert.Equal("aaaaaaaaaaa", engine.Current?.VideoId);
        Assert.Equal(0, skipped);
    }
}
