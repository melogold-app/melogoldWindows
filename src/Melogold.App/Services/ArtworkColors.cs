using Melogold.Core.Artwork;
using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Quantize;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Score;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Melogold.App.Services;

/// <summary>Цвета «Сейчас играет» из обложки: фон, текст, вторичный текст, подложка активной строки.</summary>
/// <param name="Accent">главное действие поверх цвета обложки: ▶, заполнение ползунка, включённые кнопки (Material primary)</param>
/// <param name="OnAccent">значок и текст на <paramref name="Accent"/></param>
public sealed record ArtworkPalette(Color Background, Color Text, Color SecondaryText, Color Pill, Color Accent, Color OnAccent);

/// <summary>
/// Цвета из обложки трека, как у Android (<c>ArtworkColorScheme.kt</c>, REDESIGN-M3E §4.4): чёрные полосы
/// letterbox у превью YouTube обрезаются, <c>QuantizerCelebi</c> + <c>Score</c> по уменьшенной копии, схема
/// <c>Content</c> вокруг победившего цвета. У серой обложки (цветных пикселей меньше 5 %) цвета нет — остаётся тема.
/// Фон «Сейчас играет» берёт не цвет-зерно, а саму обложку по долям цветов (<see cref="BackdropAsync"/>, tasks/0034).
/// </summary>
public static class ArtworkColors
{
    private const int SampleSize = 112;
    private const uint MaxColors = 128;
    private const double MinChroma = 8;
    private const double MinColorfulShare = 0.05;
    private const int BarMaxChannel = 24;
    private const double BarMinShare = 0.9;
    private const int MinContent = 3;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, uint?> Seeds = [];
    private static readonly Dictionary<string, BackdropCover?> Backdrops = [];

    /// <summary>Самая длинная сторона картинки для фона: обложка в 226 точек (<c>Thumbnails.Sized</c>) целиком, больше не нужно.</summary>
    private const int BackdropMaxSide = 512;

    /// <summary>Палитра для темы; null — обложка серая или не загрузилась.</summary>
    public static async Task<ArtworkPalette?> PaletteAsync(string key, string? url, bool dark, CancellationToken ct = default) =>
        await SeedOfAsync(key, url, ct) is { } seed ? Palette(seed, dark) : null;

    /// <summary>Главный цвет обложки (фон картинки «Итогов года», tasks/0015); null — обложка серая или не загрузилась.</summary>
    public static async Task<Color?> MainColorAsync(string key, string? url, CancellationToken ct = default) =>
        await SeedOfAsync(key, url, ct) is { } seed ? ToColor(seed) : null;

    private static async Task<uint?> SeedOfAsync(string key, string? url, CancellationToken ct)
    {
        if (url is null) return null;
        uint? seed;
        lock (Seeds)
        {
            if (Seeds.TryGetValue(key, out seed)) return seed;
        }
        try
        {
            var bytes = await BytesOfAsync(url, ct);
            seed = await Task.Run(() => SeedAsync(bytes), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        lock (Seeds)
        {
            if (Seeds.Count > 32) Seeds.Clear();
            Seeds[key] = seed;
        }
        return seed;
    }

    /// <summary>
    /// Картинка обложки: из кэша изображений, где она уже лежит (строка, панель плеера), в сеть заново не ходим; у кадра
    /// видео — без чёрных полей, и цвет полей не мешает.
    /// </summary>
    private static async Task<byte[]> BytesOfAsync(string url, CancellationToken ct) =>
        Images.Cache is { } cache && Uri.TryCreate(url, UriKind.Absolute, out var uri) && await cache.GetAsync(uri) is { } path
            ? await File.ReadAllBytesAsync(path, ct)
            : await Http.GetByteArrayAsync(url, ct);

    /// <summary>
    /// Обложка для фона «Сейчас играет»: 24 × 24 размытая текстура, яркость, цвета по долям и подложка. Считается в фоне,
    /// один раз на обложку; null — обложка не загрузилась.
    /// </summary>
    public static async Task<BackdropCover?> BackdropAsync(string key, string? url, CancellationToken ct = default)
    {
        if (url is null) return null;
        lock (Backdrops)
        {
            if (Backdrops.TryGetValue(key, out var known)) return known;
        }
        BackdropCover? cover;
        try
        {
            var bytes = await BytesOfAsync(url, ct);
            cover = await Task.Run(() => BackdropOfAsync(bytes), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        lock (Backdrops)
        {
            if (Backdrops.Count > 32) Backdrops.Clear();
            Backdrops[key] = cover;
        }
        return cover;
    }

    private static async Task<BackdropCover?> BackdropOfAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var (width, height) = ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        BitmapTransform? transform = null;
        if (Math.Max(width, height) > BackdropMaxSide)
        {
            var scale = BackdropMaxSide / (double)Math.Max(width, height);
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
            transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Fant };
        }
        // Без усреднения и без цветокоррекции: доли цветов считаются по точкам самой картинки
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, transform ?? new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return BackdropCover.Prepare(data.DetachPixelData(), width, height, width * 4);
    }

    private static ArtworkPalette Palette(uint seed, bool dark)
    {
        var core = CorePalette.ContentOf(seed);
        var scheme = dark ? new DarkSchemeMapper().Map(core) : new LightSchemeMapper().Map(core);
        return new ArtworkPalette(ToColor(scheme.Surface), ToColor(scheme.OnSurface), ToColor(scheme.OnSurfaceVariant), ToColor(scheme.SecondaryContainer),
            ToColor(scheme.Primary), ToColor(scheme.OnPrimary));
    }

    private static Color ToColor(uint argb) => Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static async Task<uint?> SeedAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = SampleSize;
        var height = (int)Math.Clamp(Math.Round(SampleSize * decoder.PixelHeight / (double)decoder.PixelWidth), 1, SampleSize * 2);
        var transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var raw = data.DetachPixelData();
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = 0xFF000000u | ((uint)raw[i * 4 + 2] << 16) | ((uint)raw[i * 4 + 1] << 8) | raw[i * 4];

        var quantized = QuantizerCelebi.Quantize(WithoutLetterbox(pixels, width, height), MaxColors);
        // Чёрный, белый и серые без оттенка перевесили бы долю оттенков у Score: считаются только цветные
        var total = quantized.Values.Sum(v => (long)v);
        var colorful = quantized.Where(p => Hct.FromInt(p.Key).Chroma >= MinChroma).ToDictionary(p => p.Key, p => p.Value);
        if (total == 0 || colorful.Values.Sum(v => (long)v) < total * MinColorfulShare) return null;
        var scored = Scorer.Score(colorful);
        return scored.Count > 0 ? scored[0] : null;
    }

    private static bool IsBar(uint pixel) => Math.Max((pixel >> 16) & 0xFF, Math.Max((pixel >> 8) & 0xFF, pixel & 0xFF)) <= BarMaxChannel;

    /// <summary>Пиксели без чёрных полос сверху, снизу и по бокам; почти чёрная обложка остаётся как есть.</summary>
    private static uint[] WithoutLetterbox(uint[] pixels, int width, int height)
    {
        var (top, bottom) = BarFree(height, width, (y, x) => pixels[y * width + x]);
        var rows = bottom - top + 1;
        var (left, right) = BarFree(width, rows, (x, i) => pixels[(top + i) * width + x]);
        var columns = right - left + 1;
        if (rows < MinContent || columns < MinContent) return pixels;
        var result = new uint[rows * columns];
        var n = 0;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                result[n++] = pixels[y * width + x];
        return result;
    }

    private static (int First, int Last) BarFree(int count, int length, Func<int, int, uint> pixel)
    {
        bool Bar(int line)
        {
            var bars = 0;
            for (var i = 0; i < length; i++) if (IsBar(pixel(line, i))) bars++;
            return bars >= length * BarMinShare;
        }
        int first = 0, last = count - 1;
        while (first < last && Bar(first)) first++;
        while (last > first && Bar(last)) last--;
        return (first, last);
    }
}
