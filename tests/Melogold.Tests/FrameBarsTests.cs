using Melogold.Core.Music;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Поля кадра видео (tasks/0007) и поля любого цвета с обводкой скана (tasks/0020, случаи как у Apple): срезаются
/// только настоящие поля и рамка, тёмная или однотонная сцена остаётся.
/// </summary>
public class FrameBarsTests
{
    /// <summary>Кадр BGRA: <paramref name="paint"/> даёт яркость пикселя (x, y).</summary>
    private static byte[] Frame(int width, int height, Func<int, int, byte> paint)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = paint(x, y);
                var i = (y * width + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    /// <summary>«Картинка»: пёстрая, с тёмными местами, но не поле.</summary>
    private static byte Picture(int x, int y) => (byte)(40 + (x * 7 + y * 13) % 200);

    [Fact]
    public void SquareCoverInWideFrameLosesSideBars()
    {
        // hq720 «статичного» видео: обложка 720×720 посередине кадра 1280×720
        var bgra = Frame(1280, 720, (x, y) => x is >= 280 and < 1000 ? Picture(x, y) : (byte)(x % 3 == 0 ? 12 : 4));
        Assert.Equal(new PixelRect(280, 0, 720, 720), FrameBars.Content(bgra, 1280, 720));
    }

    [Fact]
    public void LetterboxedPreviewLosesTopAndBottom()
    {
        // hqdefault 480×360: кадр 16:9 и полосы по 45 px сверху и снизу
        var bgra = Frame(480, 360, (x, y) => y is >= 45 and < 315 ? Picture(x, y) : (byte)0);
        Assert.Equal(new PixelRect(0, 45, 480, 270), FrameBars.Content(bgra, 480, 360));
    }

    [Fact]
    public void FullFrameStaysAsIs() =>
        Assert.Null(FrameBars.Content(Frame(320, 180, Picture), 320, 180));

    [Fact]
    public void DarkSceneOnOneSideIsNotABar()
    {
        // Ночная сцена: тёмная левая треть, справа светло — полей нет
        var bgra = Frame(320, 180, (x, y) => x < 110 ? (byte)6 : Picture(x, y));
        Assert.Null(FrameBars.Content(bgra, 320, 180));
    }

    [Fact]
    public void AlmostBlackFrameStaysAsIs()
    {
        // Почти весь кадр чёрный, светлая полоска посередине — срезать нечего
        var bgra = Frame(320, 180, (x, y) => x is >= 150 and < 170 ? Picture(x, y) : (byte)0);
        Assert.Null(FrameBars.Content(bgra, 320, 180));
    }

    [Fact]
    public void ThinEdgeIsNotABar()
    {
        // Чёрная кромка в 2 px — меньше 3 % стороны
        var bgra = Frame(320, 180, (x, y) => x is < 2 or >= 318 ? (byte)0 : Picture(x, y));
        Assert.Null(FrameBars.Content(bgra, 320, 180));
    }

    [Fact]
    public void JpegNoiseInBarsIsTolerated()
    {
        // Редкие светлые пиксели в поле (шум JPEG) — всё равно поле
        var bgra = Frame(320, 180, (x, y) => x is >= 70 and < 250 ? Picture(x, y) : (byte)(x == 10 && y == 50 ? 200 : 8));
        Assert.Equal(new PixelRect(70, 0, 180, 180), FrameBars.Content(bgra, 320, 180));
    }

    /// <summary>Кадр по цвету пикселя (r, g, b).</summary>
    private static byte[] Colored(int width, int height, Func<int, int, (byte R, byte G, byte B)> paint)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = paint(x, y);
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
            }
        return pixels;
    }

    private static (byte, byte, byte) Gray(byte v) => (v, v, v);

    [Fact]
    public void BrownSideBarsAreBarsToo()
    {
        // «Группа крови»: обложка посередине кадра, по бокам ровные коричневые поля с шумом JPEG
        var pixels = Colored(320, 180, (x, y) =>
        {
            if (x is >= 70 and < 250) return Gray(Picture(x, y));
            var noise = (byte)((x + y) % 9);
            return ((byte)(90 + noise), (byte)(45 + noise), (byte)(25 + noise));
        });
        Assert.Equal(new PixelRect(70, 0, 180, 180), FrameBars.Content(pixels, 320, 180));
    }

    [Fact]
    public void BlackRingInsideBrownBarsGoesToo()
    {
        // «Группа крови» целиком: коричневые поля, внутри них обложка в чёрной обводке 6 px
        var pixels = Colored(320, 180, (x, y) =>
            x is < 70 or >= 250 ? ((byte)90, (byte)45, (byte)25)
            : x < 76 || x >= 244 || y < 6 || y >= 174 ? Gray(8)
            : Gray(Picture(x, y)));
        Assert.Equal(new PixelRect(77, 7, 166, 166), FrameBars.Content(pixels, 320, 180));
    }

    [Fact]
    public void ThickFrameIsCutAsBarsNotAsARing()
    {
        // Светлые поля по 12 % с четырёх сторон — срезаются как поля
        var pixels = Colored(200, 200, (x, y) => x is >= 24 and < 176 && y is >= 24 and < 176 ? Gray(Picture(x, y)) : Gray(240));
        Assert.Equal(new PixelRect(24, 24, 152, 152), FrameBars.Content(pixels, 200, 200));
    }

    [Fact]
    public void PictureWithoutRingStays() => Assert.Null(FrameBars.Content(Frame(200, 200, Picture), 200, 200));

    [Fact]
    public void SquareCoverLosesOnlyItsRing()
    {
        // Квадратная обложка YouTube Music — скан в чёрной обводке 5 px; поля не ищутся
        var pixels = Colored(200, 200, (x, y) => x < 5 || x >= 195 || y < 5 || y >= 195 ? Gray(6) : Gray(Picture(x, y)));
        Assert.Equal(new PixelRect(6, 6, 188, 188), FrameBars.Content(pixels, 200, 200, bars: false));
    }

    [Fact]
    public void SquareCoverOnPlainBackgroundKeepsItsBackground()
    {
        // Без поиска полей широкий ровный фон обложки (25 % стороны) не срезается: это сама обложка
        var pixels = Colored(200, 200, (x, y) => x is >= 50 and < 150 && y is >= 50 and < 150 ? Gray(Picture(x, y)) : Gray(0));
        Assert.Null(FrameBars.Content(pixels, 200, 200, bars: false));
    }

    [Fact]
    public void DifferentColorsOnTheSidesAreNotBars()
    {
        // Слева коричневая стена, справа синее небо — это кадр, а не поля
        var pixels = Colored(320, 180, (x, y) => x is >= 70 and < 250 ? Gray(Picture(x, y)) : x < 70 ? ((byte)90, (byte)45, (byte)25) : ((byte)40, (byte)90, (byte)200));
        Assert.Null(FrameBars.Content(pixels, 320, 180));
    }

    [Fact]
    public void PlainSkyOnOneSideIsNotABar()
    {
        // Ровное светлое небо только слева — полей нет
        var pixels = Colored(320, 180, (x, y) => x < 110 ? ((byte)200, (byte)220, (byte)250) : Gray(Picture(x, y)));
        Assert.Null(FrameBars.Content(pixels, 320, 180));
    }

    [Fact]
    public void OldVideosFallBackToHqDefault()
    {
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg", Thumbnails.Fallback("https://i.ytimg.com/vi/jNQXAC9IVRw/hq720.jpg"));
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg", Thumbnails.Fallback("https://i.ytimg.com/vi/jNQXAC9IVRw/maxresdefault.jpg?sqp=abc"));
        Assert.Null(Thumbnails.Fallback("https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg"));
        Assert.Null(Thumbnails.Fallback("https://lh3.googleusercontent.com/abc=w544-h544-l90-rj"));
    }
}
