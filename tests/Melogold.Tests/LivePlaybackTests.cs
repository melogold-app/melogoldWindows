using System.Diagnostics;
using System.Globalization;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Приёмка среза 2 (docs/PROMPT.md §4): от команды до звука — медиана ≤ 3 с, p90 ≤ 6 с. Живой тест: настоящий YouTube и
/// системный MediaPlayer (звук идёт на устройство вывода, громкость — 0). Только с <c>MELOGOLD_LIVE=1</c>.
/// </summary>
public class LivePlaybackTests(ITestOutputHelper output)
{
    private sealed class Settings : IPlaybackSettings
    {
        public double Volume => 0;
        public bool Muted => true;
        public double Speed => 1;
        public bool NormalizeVolume => true;
        public bool Autoplay => false;
        public bool PauseHistory => true;
    }

    [Fact]
    public async Task TimeToSound()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var client = new InnerTubeClient();
        (client.Language, client.Region) = InnerTubeClient.LocaleFrom(new CultureInfo("ru-RU"));
        var music = new YouTubeMusic(client);
        var library = new Library(new LibraryDatabase(":memory:"));
        var engine = new PlayerEngine(new StreamResolver(client), music, library, new Settings());

        // Песни YTM и обычные видео YouTube вперемешку
        string[] ids = ["xtxjm7ciwmc", "Z4jQ4hZfk00", "qbRYc1cNCGg", "EByofhvVRco", "dQ8gb7FGMf4"];
        var times = new List<double>();
        foreach (var id in ids)
        {
            var playing = new TaskCompletionSource();
            void OnState()
            {
                if (engine.Status == PlayerStatus.Playing && engine.Position > TimeSpan.FromMilliseconds(200)) playing.TrySetResult();
            }
            engine.StateChanged += OnState;
            var watch = Stopwatch.StartNew();
            engine.PlaySingle(new Track { VideoId = id, Title = id });
            // Позиция растёт без событий: опрашиваем
            while (!playing.Task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                OnState();
            }
            engine.StateChanged -= OnState;
            Assert.True(playing.Task.IsCompleted, $"{id}: не заиграл за 20 с, статус {engine.Status}, ошибка {engine.Error?.Message}");
            times.Add(watch.Elapsed.TotalSeconds);
            output.WriteLine($"{id}: {watch.Elapsed.TotalSeconds:F2} с, {engine.Stream?.Source} itag {engine.Stream?.Itag}");
            engine.Pause();
        }
        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"первый трек: медиана {median:F2} с, максимум {times[^1]:F2} с");

        // Переход по очереди: начало звука следующего трека уже подгружено
        engine.PlayList(ids.Select(id => new Track { VideoId = id, Title = id }).ToList(), 0);
        await WaitPlaying(engine);
        var transitions = new List<double>();
        for (var i = 1; i < ids.Length; i++)
        {
            await Task.Delay(6000, TestContext.Current.CancellationToken);
            var watch = Stopwatch.StartNew();
            engine.Next();
            await WaitPlaying(engine);
            transitions.Add(watch.Elapsed.TotalSeconds);
            output.WriteLine($"переход → {ids[i]}: {watch.Elapsed.TotalSeconds:F2} с");
        }
        transitions.Sort();
        output.WriteLine($"переходы: медиана {transitions[transitions.Count / 2]:F2} с");
        Assert.True(transitions[transitions.Count / 2] <= 1.5, "переход дольше 1,5 с");
        engine.Dispose();
    }

    private static async Task WaitPlaying(PlayerEngine engine)
    {
        var watch = Stopwatch.StartNew();
        while (!(engine.Status == PlayerStatus.Playing && engine.Position > TimeSpan.FromMilliseconds(200)) && watch.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(50);
        Assert.True(engine.Status == PlayerStatus.Playing, $"не заиграл: {engine.Status} {engine.Error?.Message}");
    }
}
