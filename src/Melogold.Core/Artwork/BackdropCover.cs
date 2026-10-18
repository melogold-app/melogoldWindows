namespace Melogold.Core.Artwork;

/// <summary>
/// Обложка, подготовленная для фона «Сейчас играет» (tasks/0034): маленькая размытая текстура, средняя яркость и цвет
/// подложки. Считается один раз на обложку, в фоне; на экране с ней ничего не пересчитывается. Числа — как у Linux
/// (<c>backdrop_lab.rs</c>, <c>load_cover</c>): по ним сверяются все платформы.
/// </summary>
public sealed class BackdropCover
{
    /// <summary>Сторона маленькой текстуры.</summary>
    public const int Size = 24;

    /// <summary>Радиус размытия «коробкой» (окно 5 × 5) и число проходов.</summary>
    private const int BlurRadius = 2;
    private const int BlurPasses = 3;

    /// <summary>Яркость, до которой приглушается светлая обложка под белым текстом, и пределы множителя.</summary>
    private const double TargetLuma = 0.30;

    /// <summary>Насыщенность матрицы цвета.</summary>
    public const double Saturation = 1.6;

    private BackdropCover(byte[] pixels, double luma, uint backdrop, IReadOnlyList<ColorShare> shares, byte[]? fluid)
    {
        Pixels = pixels;
        FluidPixels = fluid;
        Luma = luma;
        Base = backdrop;
        Shares = shares;
    }

    /// <summary>Цвета обложки по долям, преобладающий первым.</summary>
    public IReadOnlyList<ColorShare> Shares { get; }

    /// <summary>Подложка: <see cref="ArtworkShares.DarkBackdrop"/> преобладающего цвета, <c>0xRRGGBB</c>.</summary>
    public uint Base { get; }

    /// <summary>Средняя яркость 24 × 24 до размытия (0..1): <c>(0,2126 R + 0,7152 G + 0,0722 B) / 255</c>.</summary>
    public double Luma { get; }

    /// <summary><c>blurred</c>: 24 × 24 RGBA после размытия, непрозрачная, без матрицы цвета.</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// Обложка для шейдера фона (tasks/0035 §1): 256 × 256 RGBA, уменьшенная усреднением по клеткам, без размытия и без матрицы
    /// цвета (их делает шейдер). null — картинка кривая. Мипы — <see cref="Visualizer.FluidTexture.Mips"/>.
    /// </summary>
    public byte[]? FluidPixels { get; }

    /// <summary>Яркость <c>k</c> матрицы цвета: светлую обложку приглушаем, тёмную не трогаем.</summary>
    public double Brightness => BrightnessFor(Luma);

    public static double BrightnessFor(double luma) => Math.Clamp(TargetLuma / Math.Max(luma, 0.01), TargetLuma, 1.0);

    /// <summary>
    /// Подготовка по пикселям RGBA (<paramref name="stride"/> — байт в строке): уменьшение усреднением по клеткам до
    /// 24 × 24, яркость, три прохода размытия «коробкой» 5 × 5 с повтором крайнего пикселя, цвета по долям.
    /// null — картинка пустая или кривая.
    /// </summary>
    public static BackdropCover? Prepare(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || rgba.Length < stride * (height - 1) + width * 4) return null;

        var small = new double[Size * Size * 3];
        double luma = 0;
        for (var cell = 0; cell < Size * Size; cell++)
        {
            int cx = cell % Size, cy = cell / Size;
            int x0 = cx * width / Size, x1 = Math.Min(Math.Max((cx + 1) * width / Size, x0 + 1), width);
            int y0 = cy * height / Size, y1 = Math.Min(Math.Max((cy + 1) * height / Size, y0 + 1), height);
            double r = 0, g = 0, b = 0, n = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = y * stride + x * 4;
                    r += rgba[i];
                    g += rgba[i + 1];
                    b += rgba[i + 2];
                    n++;
                }
            }
            n = Math.Max(n, 1);
            small[cell * 3] = r / n;
            small[cell * 3 + 1] = g / n;
            small[cell * 3 + 2] = b / n;
            luma += (0.2126 * small[cell * 3] + 0.7152 * small[cell * 3 + 1] + 0.0722 * small[cell * 3 + 2]) / 255;
        }
        luma /= Size * Size;

        for (var pass = 0; pass < BlurPasses; pass++) small = BoxBlur(small);

        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            pixels[i * 4] = (byte)small[i * 3];
            pixels[i * 4 + 1] = (byte)small[i * 3 + 1];
            pixels[i * 4 + 2] = (byte)small[i * 3 + 2];
            pixels[i * 4 + 3] = 255;
        }

        var shares = ArtworkShares.Shares(rgba, width, height, stride);
        // Нет цветов (обложка пустая) — почти чёрная подложка
        var dominant = shares.Count > 0 ? shares[0].Rgb : 0x101010u;
        return new BackdropCover(pixels, luma, ArtworkShares.DarkBackdrop(dominant), shares, Visualizer.FluidTexture.Downscale(rgba, width, height, stride));
    }

    /// <summary>Размытие «коробкой» 5 × 5, края — повтор крайнего пикселя.</summary>
    private static double[] BoxBlur(double[] source)
    {
        var result = new double[source.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double r = 0, g = 0, b = 0;
                for (var dy = -BlurRadius; dy <= BlurRadius; dy++)
                {
                    for (var dx = -BlurRadius; dx <= BlurRadius; dx++)
                    {
                        var i = (Math.Clamp(y + dy, 0, Size - 1) * Size + Math.Clamp(x + dx, 0, Size - 1)) * 3;
                        r += source[i];
                        g += source[i + 1];
                        b += source[i + 2];
                    }
                }
                var o = (y * Size + x) * 3;
                const double window = (2 * BlurRadius + 1) * (2 * BlurRadius + 1);
                result[o] = r / window;
                result[o + 1] = g / window;
                result[o + 2] = b / window;
            }
        }
        return result;
    }

    /// <summary>
    /// Текстура с матрицей цвета (яркость <see cref="Brightness"/>, насыщенность <see cref="Saturation"/> по Rec. 709), RGBA.
    /// Матрица линейна, поэтому применяется к 576 пикселям один раз на обложку, а не к каждому кадру: смесь двух слоёв и
    /// размытие дают тот же результат с точностью до обрезки каналов.
    /// </summary>
    public byte[] Tinted() => ApplyColorMatrix(Pixels, Brightness, Saturation);

    /// <summary>
    /// <c>R' = k·((lr+s)·R + lg·G + lb·B)</c> и так далее для G и B, где <c>lr = 0,2126·(1−s)</c>, <c>lg = 0,7152·(1−s)</c>,
    /// <c>lb = 0,0722·(1−s)</c>; результат обрезается до 0..255, альфа не меняется.
    /// </summary>
    public static byte[] ApplyColorMatrix(byte[] rgba, double brightness, double saturation)
    {
        double lr = 0.2126 * (1 - saturation), lg = 0.7152 * (1 - saturation), lb = 0.0722 * (1 - saturation);
        var result = new byte[rgba.Length];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            result[i] = Channel(brightness * ((lr + saturation) * r + lg * g + lb * b));
            result[i + 1] = Channel(brightness * (lr * r + (lg + saturation) * g + lb * b));
            result[i + 2] = Channel(brightness * (lr * r + lg * g + (lb + saturation) * b));
            result[i + 3] = rgba[i + 3];
        }
        return result;
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
