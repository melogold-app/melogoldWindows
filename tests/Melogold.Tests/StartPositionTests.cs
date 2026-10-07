using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Запомненная позиция восстановленной очереди достаётся только «Продолжить»: трек, включённый нажатием в списке,
/// начинается с нуля (07.10.2026: God's Plan с 11-й секунды после перезапуска). Сети нет — трек не играет, видна позиция,
/// с которой начата загрузка.
/// </summary>
public sealed class StartPositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-start-{Guid.NewGuid():N}");

    public StartPositionTests() => Directory.CreateDirectory(_directory);

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

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
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

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), what);
    }

    private PlayerEngine Engine()
    {
        var client = new InnerTubeClient(new NoNetwork());
        return new PlayerEngine(new StreamResolver(client) { Clients = [ClientProfile.VisionOs] }, new YouTubeMusic(client),
            new Library(new LibraryDatabase(Path.Combine(_directory, $"{Guid.NewGuid():N}.db"))), new Settings());
    }

    private static Track Track(string id) => new() { VideoId = id, Title = id };

    [Fact]
    public async Task ATrackStartedAnewDoesNotTakeTheRestoredPosition()
    {
        using var engine = Engine();
        engine.Restore(new QueueSnapshot([new QueueItem(Track("aaaaaaaaaaa"), false, 1)], 0, null, 11_392));
        engine.PlayList([Track("bbbbbbbbbbb")], 0);
        await WaitFor(() => engine.Current?.VideoId == "bbbbbbbbbbb" && engine.Status != PlayerStatus.Resolving, "трек загружается");
        Assert.Equal(0, engine.LastLoadStartMs);
    }

    [Fact]
    public async Task PlayResumesTheRestoredTrackFromItsPosition()
    {
        using var engine = Engine();
        engine.Restore(new QueueSnapshot([new QueueItem(Track("aaaaaaaaaaa"), false, 1)], 0, null, 11_392));
        engine.Play();
        await WaitFor(() => engine.Status != PlayerStatus.Resolving && engine.Status != PlayerStatus.Paused, "трек загружается");
        Assert.Equal(11_392, engine.LastLoadStartMs);
    }
}
