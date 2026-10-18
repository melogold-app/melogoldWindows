using MaterialColorUtilities.Quantize;

namespace Melogold.Core.Artwork;

/// <summary>Цвет обложки и его доля в ней (0..1).</summary>
public readonly record struct ColorShare(uint Rgb, double Share);

/// <summary>
/// Цвета обложки по долям и цвет подложки фона «Сейчас играет» (tasks/0034). Числа и порядок шагов — как у Linux
/// (<c>melogold-core</c> <c>artwork_colors::shares</c> и <c>dark_backdrop</c>), по ним сверяются все платформы.
/// Цвет — <c>0xRRGGBB</c>.
/// </summary>
public static class ArtworkShares
{
    /// <summary>Точек по ширине; строк — пропорционально, но не больше <see cref="MaxRows"/>.</summary>
    private const int Columns = 112;
    private const int MaxRows = 224;
    private const uint MaxColors = 128;

    /// <summary>Сколько цветов фона: преобладающий и до пяти остальных.</summary>
    private const int MaxShares = 6;

    /// <summary>
    /// Цвет меньше этой доли не рисуется: шум сжатия и точка в углу — не цвета обложки. Порог низкий намеренно: радуга
    /// «The Dark Side of the Moon» — 1–2 % на цвет, и без неё обложка — просто чёрная.
    /// </summary>
    private const double MinShare = 0.005;

    /// <summary>Расстояние в Lab (ΔE76), ближе которого цвета сливаются в один: тени и блики одного цвета — один цвет.</summary>
    private const double MergeDistance = 16;

    /// <summary>
    /// Цвета обложки по долям, преобладающий — первым; доли в сумме дают 1; пусто — если картинка пустая или кривая.
    /// <paramref name="rgba"/> — пиксели RGBA, <paramref name="stride"/> — байт в строке.
    /// </summary>
    /// <remarks>
    /// В отличие от Score здесь нет «лучшего» цвета: Score любит яркое, и у «The Dark Side of the Moon» фон выходил красным
    /// из тонкой радуги на чёрном. Чёрный и серый — такие же цвета, как остальные. Полосы letterbox не срезаются: чёрный край —
    /// часть рисунка.
    /// </remarks>
    public static IReadOnlyList<ColorShare> Shares(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || rgba.Length < stride * (height - 1) + width * 4) return [];

        // Точки, а не средние по клеткам: усреднение превращает мелкий цветной узор в серый, и доля серого росла из ничего
        var rows = (int)Math.Clamp(Math.Round(Columns * (double)height / width, MidpointRounding.AwayFromZero), 1, MaxRows);
        var pixels = new uint[Columns * rows];
        for (var y = 0; y < rows; y++)
        {
            var line = Math.Min(y * height / rows, height - 1) * stride;
            for (var x = 0; x < Columns; x++)
            {
                var i = line + Math.Min(x * width / Columns, width - 1) * 4;
                pixels[y * Columns + x] = 0xFF000000u | ((uint)rgba[i] << 16) | ((uint)rgba[i + 1] << 8) | rgba[i + 2];
            }
        }

        // По убыванию числа пикселей, при равенстве — по значению RGB
        var colors = QuantizerCelebi.Quantize(pixels, MaxColors)
            .Select(p => (Rgb: p.Key & 0xFFFFFF, Count: (double)p.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Rgb)
            .ToList();

        // Слияние близких: цвет группы — её самый частый цвет, без усреднения (среднее в Lab уплывало к серому, и радуга
        // «A Head Full of Dreams» превращалась в серую кашу)
        var clusters = new List<Cluster>();
        foreach (var (rgb, count) in colors)
        {
            var lab = ToLab(rgb);
            if (clusters.Find(c => Distance(c.Lab, lab) < MergeDistance) is { } found) found.Weight += count;
            else clusters.Add(new Cluster(lab, rgb, count));
        }

        // OrderByDescending устойчива: при равном весе порядок появления сохраняется
        var ordered = clusters.OrderByDescending(c => c.Weight).ToList();
        var total = ordered.Sum(c => c.Weight);
        if (total <= 0) return [];
        var kept = ordered
            .Select(c => new ColorShare(c.Rgb, c.Weight / total))
            .Where(c => c.Share >= MinShare)
            .Take(MaxShares)
            .ToList();
        var sum = kept.Sum(c => c.Share);
        return kept.Select(c => c with { Share = c.Share / sum }).ToList();
    }

    /// <summary>
    /// Цвет подложки под светлый текст: оттенок и насыщенность прежние, светлота — в пределах, где белый текст читается
    /// (<c>L*</c> от 6 до 38). Белая обложка даёт серый фон, чёрная — почти чёрный, красная остаётся красной.
    /// </summary>
    public static uint DarkBackdrop(uint rgb)
    {
        var lab = ToLab(rgb);
        var target = Math.Clamp(lab[0], 6, 38);
        // Темнее — чуть меньше насыщенности (у тёмного цвета та же насыщенность кричит), но не вдвое: иначе радуга
        // превращается в грязь
        var keep = lab[0] > 0 ? Math.Clamp(Math.Sqrt(target / lab[0]), 0.7, 1) : 1;
        return FromLab([target, lab[1] * keep, lab[2] * keep]);
    }

    /// <summary>CIE Lab (D65) по sRGB.</summary>
    internal static double[] ToLab(uint rgb)
    {
        static double Channel(uint rgb, int shift)
        {
            var c = ((rgb >> shift) & 0xFF) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double r = Channel(rgb, 16), g = Channel(rgb, 8), b = Channel(rgb, 0);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
    }

    /// <summary>sRGB по CIE Lab (D65), с округлением до байта.</summary>
    internal static uint FromLab(double[] lab)
    {
        var fy = (lab[0] + 16) / 116;
        double fx = fy + lab[1] / 500, fz = fy - lab[2] / 200;
        static double Inverse(double t) => t * t * t > 216.0 / 24389.0 ? t * t * t : (116 * t - 16) / (24389.0 / 27.0);
        double x = Inverse(fx) * 0.95047, y = Inverse(fy), z = Inverse(fz) * 1.08883;
        var linear = new[]
        {
            3.2406 * x - 1.5372 * y - 0.4986 * z,
            -0.9689 * x + 1.8758 * y + 0.0415 * z,
            0.0557 * x - 0.2040 * y + 1.0570 * z,
        };
        static uint Gamma(double c)
        {
            c = Math.Clamp(c, 0, 1);
            var v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return (uint)Math.Round(v * 255, MidpointRounding.AwayFromZero);
        }
        return (Gamma(linear[0]) << 16) | (Gamma(linear[1]) << 8) | Gamma(linear[2]);
    }

    private sealed class Cluster(double[] lab, uint rgb, double weight)
    {
        public double[] Lab { get; } = lab;
        public uint Rgb { get; } = rgb;
        public double Weight { get; set; } = weight;
    }

    private static double Distance(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
}
