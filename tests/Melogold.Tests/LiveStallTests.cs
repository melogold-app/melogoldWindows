using System.Diagnostics;
using System.Globalization;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// tasks/0021: «трек не начинает играть, пока не подвинешь ползунок». Настоящий MediaPlayer и вывод звука (в тихом
/// режиме — без звука и без медиапанели Windows), сценарии задания по нескольку раз: трек целиком в кэше с нуля,
/// восстановленная очередь с позицией, трек с кэшем только начала, переключение с играющего трека. Запросов
/// <c>player</c> — по одному на трек. Только с <c>MELOGOLD_LIVE=1</c> и <c>MELOGOLD_QUIET=1</c>.
/// </summary>
public class LiveStallTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"melogold-stall-{Guid.NewGuid():N}")).FullName;

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

    private sealed class Settings : IPlaybackSettings
    {
        public double Volume => 0;
        public bool Muted => true;
        public double Speed => 1;
        public bool NormalizeVolume => false;
        public bool Autoplay => false;
        public bool PauseHistory => true;
    }

    /// <summary>Сколько до звука (позиция ушла дальше 300 мс при «Играет»); null — не заиграл за 10 с.</summary>
    private async Task<double?> StartAsync(PlayerEngine engine, string what, Action start, long fromMs = 0)
    {
        var watch = Stopwatch.StartNew();
        start();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            if (engine.Status == PlayerStatus.Playing && engine.Position.TotalMilliseconds > fromMs + 300)
            {
                output.WriteLine($"✓ {what}: {watch.Elapsed.TotalSeconds:F2} с, {engine.Stream?.Source}");
                return watch.Elapsed.TotalSeconds;
            }
        }
        output.WriteLine($"✗ {what}: не заиграл за 10 с — статус {engine.Status}, позиция {engine.Position.TotalMilliseconds:F0} мс, ошибка {engine.Error?.Message ?? "-"}");
        // Что меняет перемотка
        var at = engine.Position;
        engine.Seek(at + TimeSpan.FromSeconds(2));
        await Task.Delay(3000, TestContext.Current.CancellationToken);
        output.WriteLine($"   после перемотки: статус {engine.Status}, позиция {engine.Position.TotalMilliseconds:F0} мс");
        return null;
    }

    [Fact]
    public async Task TracksStartWithoutTouchingTheSeekBar()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1" && QuietMode.IsOn, "MELOGOLD_LIVE=1 и MELOGOLD_QUIET=1");
        var client = new InnerTubeClient();
        (client.Language, client.Region) = InnerTubeClient.LocaleFrom(new CultureInfo("ru-RU"));
        var music = new YouTubeMusic(client);
        var library = new Library(new LibraryDatabase(Path.Combine(_directory, "library.db")));
        var resolver = new StreamResolver(client, line => output.WriteLine(line));
        var songs = new SongCache(Path.Combine(_directory, "songs"), () => long.MaxValue, (m, e) => output.WriteLine($"{m} {e?.Message}"));
        var downloads = new TrackDownloads(new SongCache(Path.Combine(_directory, "downloads"), () => long.MaxValue, (_, _) => { }), songs, resolver, library, (_, _) => { });
        using var engine = new PlayerEngine(resolver, music, library, new Settings()) { Songs = songs };

        var whole = new Track { VideoId = "xtxjm7ciwmc", Title = "Группа крови" };
        var partial = new Track { VideoId = "Z4jQ4hZfk00", Title = "B" };
        var failures = 0;

        // Трек целиком в кэше (как скачанный): с нуля, 5 раз
        await downloads.ReadWholeAsync(whole.VideoId, TestContext.Current.CancellationToken);
        Assert.True(songs.IsComplete(whole.VideoId));
        for (var i = 1; i <= 5; i++)
        {
            if (await StartAsync(engine, $"целиком в кэше, с нуля #{i}", () => engine.PlaySingle(whole)) is null) failures++;
            engine.Pause();
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        engine.Pause();

        // Как после перезапуска — новый плеер: восстановленная очередь с позицией и первый трек из кэша, по 5 раз
        for (var i = 1; i <= 5; i++)
        {
            using (var restarted = new PlayerEngine(resolver, music, library, new Settings()) { Songs = songs })
            {
                restarted.Restore(new QueueSnapshot([new QueueItem(whole, false, i)], 0, null, 30_000));
                await Task.Delay(300, TestContext.Current.CancellationToken);
                if (await StartAsync(restarted, $"после перезапуска: восстановленная очередь с 0:30 #{i}", restarted.Play, 30_000) is null) failures++;
                restarted.Pause();
            }
            using (var restarted = new PlayerEngine(resolver, music, library, new Settings()) { Songs = songs })
            {
                if (await StartAsync(restarted, $"после перезапуска: трек из кэша с нуля #{i}", () => restarted.PlaySingle(whole)) is null) failures++;
                restarted.Pause();
            }
        }

        // Переключение с играющего трека на трек целиком в кэше; второй сначала с кэшем только начала (заготовка)
        engine.PlayList([whole, partial], 0);
        await StartAsync(engine, "первый трек списка", () => { });
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        if (await StartAsync(engine, "следующий: в кэше только начало", () => engine.Next()) is null) failures++;
        for (var i = 1; i <= 5; i++)
        {
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            if (await StartAsync(engine, $"с играющего на целиком в кэше #{i}", () => engine.PlaySingle(whole)) is null) failures++;
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            if (await StartAsync(engine, $"обратно на второй #{i}", () => engine.PlaySingle(partial)) is null) failures++;
        }
        engine.Pause();
        Assert.Equal(0, failures);
    }
}
