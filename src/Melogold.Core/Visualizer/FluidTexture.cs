namespace Melogold.Core.Visualizer;

/// <summary>
/// Текстура обложки для шейдера фона (tasks/0035 §1): 256 × 256 RGBA — обложка, уменьшенная усреднением по клеткам (как 24 × 24
/// в tasks/0034, только 256), и полная цепочка мип-уровней стандартным усреднением 2 × 2 (<c>glGenerateMipmap</c> Linux).
/// Уровни считаются здесь, а не видеокартой: одно и то же на любой машине, и шейдер читает их трилинейно.
/// </summary>
public static class FluidTexture
{
    /// <summary>Сторона уровня 0.</summary>
    public const int Side = 256;

    /// <summary>Уровней в цепочке: 256, 128, …, 1.</summary>
    public const int Levels = 9;

    /// <summary>Обложка 256 × 256: каждая клетка — среднее своих пикселей (целочисленное, как у Linux). null — картинка кривая.</summary>
    public static byte[]? Downscale(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || rgba.Length < stride * (height - 1) + width * 4) return null;
        var pixels = new byte[Side * Side * 4];
        for (var y = 0; y < Side; y++)
        {
            int y0 = y * height / Side, y1 = Math.Min(Math.Max((y + 1) * height / Side, y0 + 1), height);
            for (var x = 0; x < Side; x++)
            {
                int x0 = x * width / Side, x1 = Math.Min(Math.Max((x + 1) * width / Side, x0 + 1), width);
                uint r = 0, g = 0, b = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = yy * stride + xx * 4;
                        r += rgba[i];
                        g += rgba[i + 1];
                        b += rgba[i + 2];
                        n++;
                    }
                }
                n = Math.Max(n, 1);
                var o = (y * Side + x) * 4;
                pixels[o] = (byte)(r / n);
                pixels[o + 1] = (byte)(g / n);
                pixels[o + 2] = (byte)(b / n);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>
    /// Цепочка уровней от 256 × 256 до 1 × 1: каждый пиксель — среднее четырёх пикселей уровня выше (с округлением). Нулевой
    /// элемент — сам <paramref name="level0"/>.
    /// </summary>
    public static byte[][] Mips(byte[] level0)
    {
        if (level0.Length != Side * Side * 4) throw new ArgumentException("Нужны 256 × 256 RGBA", nameof(level0));
        var chain = new byte[Levels][];
        chain[0] = level0;
        for (var level = 1; level < Levels; level++)
        {
            var side = Side >> level;
            var above = chain[level - 1];
            var source = side * 2;
            var current = new byte[side * side * 4];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        var i = ((2 * y) * source + 2 * x) * 4 + c;
                        var sum = above[i] + above[i + 4] + above[i + source * 4] + above[i + source * 4 + 4];
                        current[(y * side + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            }
            chain[level] = current;
        }
        return chain;
    }
}
