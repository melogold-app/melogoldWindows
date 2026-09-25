using Melogold.Core.Music;
using Xunit;

namespace Melogold.Tests;

/// <summary>Чёрные поля кадра видео (tasks/0007): срезаются только настоящие поля, тёмная сцена остаётся.</summary>
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
}
