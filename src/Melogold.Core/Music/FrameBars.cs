namespace Melogold.Core.Music;

/// <summary>Прямоугольник в пикселях.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Чёрные поля у кадра видео YouTube: квадратная обложка в кадре 16:9 (видео-«статика» с обложкой сингла) — полосы
/// по бокам, превью 4:3 (<c>hqdefault</c>) — сверху и снизу. Поля срезаются, чтобы обложка была без них.
/// Порог строгий, в отличие от цвета по обложке (<c>ArtworkColors</c>): тёмная сцена самого видео полем не считается —
/// поля почти целиком чёрные, заметные и одинаковые с двух сторон.
/// </summary>
public static class FrameBars
{
    /// <summary>Чёрный у JPEG — 0…20 по каналу.</summary>
    private const int MaxChannel = 28;

    /// <summary>Линия — поле, если почти все её пиксели чёрные.</summary>
    private const double MinBarShare = 0.98;

    /// <summary>Поля срезаются, только если вместе они не меньше 3 % стороны.</summary>
    private const double MinBars = 0.03;

    /// <summary>Поля по краям одинаковые: у тёмной сцены тёмен обычно один край.</summary>
    private const double MaxAsymmetry = 0.03;

    /// <summary>Остаток — не меньше 40 % стороны: почти чёрный кадр остаётся как есть.</summary>
    private const double MinContent = 0.4;

    /// <summary>Что остаётся без полей; null — полей нет. <paramref name="bgra"/> — пиксели BGRA8 строками без отступов.</summary>
    public static PixelRect? Content(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        int top = 0, bottom = height - 1;
        while (top < bottom && Line(bgra, width, top, 0, width - 1, horizontal: true)) top++;
        while (bottom > top && Line(bgra, width, bottom, 0, width - 1, horizontal: true)) bottom--;
        var (y0, y1) = Accept(top, height - 1 - bottom, height) ? (top, bottom) : (0, height - 1);

        int left = 0, right = width - 1;
        while (left < right && Line(bgra, width, left, y0, y1, horizontal: false)) left++;
        while (right > left && Line(bgra, width, right, y0, y1, horizontal: false)) right--;
        var (x0, x1) = Accept(left, width - 1 - right, width) ? (left, right) : (0, width - 1);

        if (x0 == 0 && y0 == 0 && x1 == width - 1 && y1 == height - 1) return null;
        return new PixelRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static bool Accept(int first, int last, int size) =>
        first + last >= size * MinBars
        && Math.Abs(first - last) <= size * MaxAsymmetry
        && size - first - last >= size * MinContent;

    /// <summary>Строка <paramref name="index"/> (или столбец) от <paramref name="from"/> до <paramref name="to"/> — поле.</summary>
    private static bool Line(ReadOnlySpan<byte> bgra, int width, int index, int from, int to, bool horizontal)
    {
        var count = to - from + 1;
        var allowed = count - (int)Math.Ceiling(count * MinBarShare);
        var bright = 0;
        for (var i = from; i <= to; i++)
        {
            var offset = (horizontal ? index * width + i : i * width + index) * 4;
            if (Math.Max(bgra[offset], Math.Max(bgra[offset + 1], bgra[offset + 2])) > MaxChannel && ++bright > allowed) return false;
        }
        return true;
    }
}
