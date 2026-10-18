using Melogold.Core.Artwork;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Фон «Сейчас играет» (tasks/0034): цвета обложки по долям, подложка, подготовка обложки 24 × 24, движение слоёв, часы
/// и размеры. Сценарии и числа те же, что у Linux (<c>artwork_colors.rs</c>, <c>backdrop_lab.rs</c>).
/// </summary>
public class BackdropTests
{
    /// <summary>Картинка RGBA: <paramref name="pixel"/> даёт цвет пикселя (x, y).</summary>
    private static byte[] Image(int width, int height, Func<int, int, byte[]> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = pixel(x, y);
                var i = (y * width + x) * 4;
                rgba[i] = color[0];
                rgba[i + 1] = color[1];
                rgba[i + 2] = color[2];
                rgba[i + 3] = 255;
            }
        }
        return rgba;
    }

    private static double Lightness(uint rgb) => ArtworkShares.ToLab(rgb)[0];

    /// <summary>
    /// Чёрная обложка с тонкой радугой (как у «The Dark Side of the Moon»): преобладает чёрный, а не красный, как выбирал
    /// Score.
    /// </summary>
    [Fact]
    public void A_black_cover_with_a_thin_rainbow_is_mostly_black()
    {
        byte[][] rainbow = [[230, 30, 30], [240, 200, 20], [30, 160, 60], [40, 90, 220]];
        var rgba = Image(200, 200, (x, y) => y is >= 90 and < 110 && x > 100 ? rainbow[(y - 90) / 5] : [2, 2, 2]);

        var shares = ArtworkShares.Shares(rgba, 200, 200, 800);

        Assert.True(Lightness(shares[0].Rgb) < 10, "первым — чёрный");
        Assert.True(shares[0].Share > 0.85, $"доля чёрного {shares[0].Share}");
        Assert.True(shares.Count >= 3, "цвета радуги тоже есть, маленькими долями");
        Assert.InRange(shares.Sum(c => c.Share), 1 - 1e-9, 1 + 1e-9);
    }

    [Fact]
    public void Shares_are_sorted_and_the_most_frequent_color_stays_unaveraged()
    {
        // Три четверти красного (с лёгким шумом — слитым в один цвет) и четверть синего
        var rgba = Image(120, 120, (x, y) => x < 90 ? [(byte)(200 + (x + y) % 3), 20, 20] : [20, 20, 200]);

        var shares = ArtworkShares.Shares(rgba, 120, 120, 480);

        Assert.Equal(2, shares.Count);
        Assert.InRange(shares[0].Share, 0.7, 0.8);
        Assert.True(shares[0].Share >= shares[1].Share);
        Assert.True(((shares[0].Rgb >> 16) & 0xFF) >= 195 && (shares[0].Rgb & 0xFF) <= 30, $"красный: {shares[0].Rgb:X6}");
        Assert.True((shares[1].Rgb & 0xFF) >= 195, $"синий: {shares[1].Rgb:X6}");
    }

    [Fact]
    public void Shares_of_a_broken_image_are_empty()
    {
        Assert.Empty(ArtworkShares.Shares([], 0, 0, 0));
        Assert.Empty(ArtworkShares.Shares(new byte[10], 4, 4, 16));
    }

    [Fact]
    public void Backdrop_keeps_white_text_readable()
    {
        Assert.True(Lightness(ArtworkShares.DarkBackdrop(0xFFFFFF)) <= 38.5, "белая обложка — серый фон");
        Assert.True(Lightness(ArtworkShares.DarkBackdrop(0x020202)) >= 5.5, "чёрная — почти чёрный, не провал");
        var red = ArtworkShares.ToLab(ArtworkShares.DarkBackdrop(0xE01E1E));
        Assert.True(red[1] > 20, $"красный остаётся красным: a* = {red[1]}");
    }

    [Fact]
    public void Lab_round_trip()
    {
        foreach (var color in new uint[] { 0x000000, 0xFFFFFF, 0xE01E1E, 0x2D5AE0, 0x7F7F7F, 0x12AB34 })
        {
            var back = ArtworkShares.FromLab(ArtworkShares.ToLab(color));
            foreach (var shift in new[] { 16, 8, 0 })
                Assert.True(Math.Abs((int)((color >> shift) & 0xFF) - (int)((back >> shift) & 0xFF)) <= 1, $"{color:X6} → {back:X6}");
        }
    }

    [Fact]
    public void A_solid_cover_prepares_a_solid_24_by_24_texture_and_its_luma()
    {
        var rgba = Image(300, 200, (_, _) => [100, 150, 200]);

        var cover = BackdropCover.Prepare(rgba, 300, 200, 1200);

        Assert.NotNull(cover);
        Assert.Equal(24 * 24 * 4, cover.Pixels.Length);
        for (var i = 0; i < cover.Pixels.Length; i += 4)
            Assert.Equal(new byte[] { 100, 150, 200, 255 }, cover.Pixels[i..(i + 4)]);
        Assert.Equal((0.2126 * 100 + 0.7152 * 150 + 0.0722 * 200) / 255, cover.Luma, 9);
        Assert.Single(cover.Shares);
        Assert.Equal(ArtworkShares.DarkBackdrop(cover.Shares[0].Rgb), cover.Base);
    }

    [Fact]
    public void The_texture_is_blurred_but_the_luma_is_taken_before_the_blur()
    {
        // Левая половина чёрная, правая белая: яркость 0,5, а на шве — плавный переход
        var rgba = Image(240, 240, (x, _) => x < 120 ? [0, 0, 0] : [255, 255, 255]);

        var cover = BackdropCover.Prepare(rgba, 240, 240, 960);

        Assert.NotNull(cover);
        Assert.Equal(0.5, cover.Luma, 6);
        byte Red(int x) => cover.Pixels[(12 * 24 + x) * 4];
        Assert.Equal(0, Red(0));
        Assert.Equal(255, Red(23));
        var seam = Enumerable.Range(8, 8).Select(Red).ToArray();
        Assert.True(seam.Zip(seam.Skip(1), (a, b) => a <= b).All(ok => ok), "переход возрастает");
        Assert.Contains(seam, v => v is > 20 and < 235);
    }

    [Fact]
    public void Color_matrix_dims_a_light_cover_and_leaves_a_dark_one()
    {
        Assert.Equal(0.30, BackdropCover.BrightnessFor(1.0), 9);
        Assert.Equal(1.0, BackdropCover.BrightnessFor(0.30), 9);
        Assert.Equal(0.6, BackdropCover.BrightnessFor(0.5), 9);
        Assert.Equal(1.0, BackdropCover.BrightnessFor(0.0), 9);

        // Белая обложка под матрицей — серая 0,3 (76/77), серый цвет насыщенность не меняет
        var white = BackdropCover.Prepare(Image(48, 48, (_, _) => [255, 255, 255]), 48, 48, 192);
        Assert.NotNull(white);
        var tinted = white.Tinted();
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, tinted[..4]);

        // Тёмная — k = 1; красный насыщеннее: зелёный и синий уходят вниз
        var dark = BackdropCover.Prepare(Image(48, 48, (_, _) => [60, 20, 20]), 48, 48, 192);
        Assert.NotNull(dark);
        Assert.Equal(1.0, dark.Brightness, 9);
        var red = dark.Tinted();
        Assert.True(red[0] > 60 && red[1] < 20 && red[2] < 20, $"{red[0]},{red[1]},{red[2]}");
    }

    [Fact]
    public void Layers_move_by_the_numbers_of_the_task()
    {
        var first = BackdropMotion.First(10, 1000, 600);
        Assert.Equal(500, first.CenterX);
        Assert.Equal(300, first.CenterY);
        Assert.Equal(1600, first.Side, 9);
        Assert.Equal(24, first.Degrees, 9);
        Assert.Equal(1, first.Opacity);

        var second = BackdropMotion.Second(0, 1000, 600);
        Assert.Equal(500, second.CenterX, 9);
        Assert.Equal(300 + 0.15 * 600, second.CenterY, 9);
        Assert.Equal(1150, second.Side, 9);
        Assert.Equal(90, second.Degrees, 9);
        Assert.Equal(0.7, second.Opacity);

        var later = BackdropMotion.Second(17 * Math.PI / 2, 1000, 600);
        Assert.Equal(500 + 0.18 * 1000, later.CenterX, 9);
        Assert.Equal(90 - 17 * Math.PI / 2 * 3.6, later.Degrees, 9);
    }

    [Fact]
    public void The_clock_runs_only_when_everything_allows_and_a_pause_keeps_the_time()
    {
        Assert.True(BackdropClock.ShouldRun(playing: true, windowVisible: true, liveBackground: true, animationsEnabled: true));
        Assert.False(BackdropClock.ShouldRun(false, true, true, true));
        Assert.False(BackdropClock.ShouldRun(true, false, true, true));
        Assert.False(BackdropClock.ShouldRun(true, true, false, true));
        Assert.False(BackdropClock.ShouldRun(true, true, true, false));

        var clock = new BackdropClock();
        clock.Advance(TimeSpan.FromSeconds(2), running: true);
        clock.Advance(TimeSpan.FromSeconds(5), running: false);
        Assert.Equal(2, clock.Seconds, 9);
        clock.Advance(TimeSpan.FromMilliseconds(500), running: true);
        Assert.Equal(2.5, clock.Seconds, 9);
    }

    [Fact]
    public void Lyrics_size_depends_on_the_height_of_the_whole_area()
    {
        Assert.Equal(18, NowPlayingMetrics.LyricsFontSize(300), 9);
        Assert.Equal(0.042 * 800, NowPlayingMetrics.LyricsFontSize(800), 9);
        Assert.Equal(64, NowPlayingMetrics.LyricsFontSize(2000), 9);
    }

    [Fact]
    public void The_lyrics_column_is_about_28_characters_wide_and_grows_with_the_font()
    {
        Assert.Equal(28 * 0.55 * 28 + 32, NowPlayingMetrics.LyricsMaxWidth(28), 9);
        Assert.Equal(2 * NowPlayingMetrics.LyricsMaxWidth(28), NowPlayingMetrics.LyricsMaxWidth(56), 9);
    }

    [Fact]
    public void The_wide_cover_side_is_the_smaller_of_half_the_height_and_38_percent_of_the_width()
    {
        Assert.Equal(400, NowPlayingMetrics.WideCoverSide(2000, 800), 9);
        Assert.Equal(0.38 * 1000, NowPlayingMetrics.WideCoverSide(1000, 800), 9);
    }
}
