using Melogold.Core.Music;
using Melogold.InnerTube;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// tasks/0020 на настоящих картинках: «Кино — Группа крови» — квадрат без полей и рамки. Один поиск и картинки, без
/// запросов <c>player</c>. Только с <c>MELOGOLD_LIVE=1</c>.
/// </summary>
public class LiveCoverTests(ITestOutputHelper output)
{
    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    [Fact]
    public async Task GruppaKroviIsASquareWithoutFrame()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var music = new YouTubeMusic(new InnerTubeClient());
        var page = await music.SearchAsync("Кино Группа крови", MusicSearchFilter.Songs, TestContext.Current.CancellationToken);
        var song = page.Items.OfType<Track>().First(t => t.Title.Contains("Группа крови", StringComparison.OrdinalIgnoreCase));
        using var http = new HttpClient();
        foreach (var (url, bars) in new[] { (Thumbnails.Sized(song.ThumbnailUrl, 544)!, false), (Thumbnails.ForVideo(song.VideoId), true) })
        {
            byte[] bytes;
            try
            {
                bytes = await http.GetByteArrayAsync(url, TestContext.Current.CancellationToken);
            }
            catch (HttpRequestException) when (Thumbnails.Fallback(url) is { } smaller)
            {
                bytes = await http.GetByteArrayAsync(smaller, TestContext.Current.CancellationToken);
            }
            var (pixels, width, height) = await DecodeAsync(bytes);
            var content = FrameBars.Content(pixels, width, height, bars);
            output.WriteLine($"{url}: {width}×{height} → {content?.ToString() ?? "как есть"}");
            if (bars)
            {
                // Кадр видео: после срезки — почти квадрат
                var rect = content ?? new PixelRect(0, 0, width, height);
                Assert.InRange(rect.Width / (double)rect.Height, 0.9, 1.12);
            }
        }
    }

    /// <summary>
    /// Большая обложка «Сейчас играет» (720 px): скачать и обработать — доли секунды; на обработку (разбор, поиск полей и
    /// обводки) уходит мало, основное время — сеть.
    /// </summary>
    [Fact]
    public async Task BigCoverIsProcessedQuickly()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var music = new YouTubeMusic(new InnerTubeClient());
        var page = await music.SearchAsync("Saba Photosynthesis", MusicSearchFilter.Songs, TestContext.Current.CancellationToken);
        var song = page.Items.OfType<Track>().First();
        using var http = new HttpClient();
        var url = Thumbnails.Sized(song.ThumbnailUrl, 720)!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = await http.GetByteArrayAsync(url, TestContext.Current.CancellationToken);
        var download = watch.ElapsedMilliseconds;
        watch.Restart();
        var (pixels, width, height) = await DecodeAsync(bytes);
        var decode = watch.ElapsedMilliseconds;
        watch.Restart();
        var content = FrameBars.Content(pixels, width, height, bars: false);
        var bars = watch.ElapsedMilliseconds;
        output.WriteLine($"{song.Title}: {width}×{height}, скачать {download} мс, разобрать {decode} мс, поля и обводка {bars} мс → {content?.ToString() ?? "как есть"}");
        Assert.True(decode + bars < 1000, $"{decode + bars} мс");
    }
}
