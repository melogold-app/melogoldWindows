namespace Melogold.Core.Music;

/// <summary>Прямоугольник в пикселях.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Поля и обводка у обложки (tasks/0007, tasks/0020; как <c>FrameBars.swift</c> Apple). Кадр видео YouTube: квадратная
/// обложка в кадре 16:9 (видео-«статика» с обложкой сингла) — полосы по бокам, превью 4:3 (<c>hqdefault</c>) — сверху и
/// снизу; поля бывают любого ровного цвета, не только чёрные (коричневые у «Группы крови»). Внутри полей, а у квадратной
/// обложки YouTube Music — прямо по краю, бывает обводка скана: рамка одного цвета со всех четырёх сторон.
/// Порог строгий, в отличие от цвета по обложке (<c>ArtworkColors</c>): тёмная или однотонная сцена самого видео полем
/// не считается — поля почти целиком одного цвета, заметные, одинаковые и одного цвета с двух сторон.
/// </summary>
public static class FrameBars
{
    /// <summary>Отклонение пикселя поля от цвета поля по каналу: шум JPEG.</summary>
    private const int Tolerance = 24;

    /// <summary>Линия — поле, если почти все её пиксели цвета поля.</summary>
    private const double MinBarShare = 0.98;

    /// <summary>Поля срезаются, только если вместе они не меньше 3 % стороны.</summary>
    private const double MinBars = 0.03;

    /// <summary>Поля по краям одинаковые: у тёмной сцены тёмен обычно один край.</summary>
    private const double MaxAsymmetry = 0.03;

    /// <summary>Поля с двух сторон одного цвета (по каналу): стена слева и небо справа — не поля.</summary>
    private const int MaxSidesDifference = 40;

    /// <summary>Остаток — не меньше 40 % стороны: почти однотонный кадр остаётся как есть.</summary>
    private const double MinContent = 0.4;

    /// <summary>Каждая сторона обводки — не шире этой доли стороны: широкий ровный фон — сама обложка.</summary>
    private const double MaxRing = 0.06;

    private readonly record struct Color(int R, int G, int B)
    {
        public bool Near(Color other, int limit) => Math.Abs(R - other.R) <= limit && Math.Abs(G - other.G) <= limit && Math.Abs(B - other.B) <= limit;
    }

    /// <summary>
    /// Что остаётся без полей и обводки; null — срезать нечего. <paramref name="pixels"/> — 4 байта на пиксель (BGRA или
    /// RGBA, альфа последней) строками без отступов. <paramref name="bars"/>: false — только обводка (обложка YouTube
    /// Music квадратная, полей у неё нет, а рамка скана бывает).
    /// </summary>
    public static PixelRect? Content(ReadOnlySpan<byte> pixels, int width, int height, bool bars = true)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) return null;
        var image = new Image(pixels, width);

        var (y0, y1) = (bars ? Bars(image, height, 0, width - 1, horizontal: true) : null) ?? (0, height - 1);
        var (x0, x1) = (bars ? Bars(image, width, y0, y1, horizontal: false) : null) ?? (0, width - 1);
        if (Ring(image, x0, x1, y0, y1) is { } inner) (x0, x1, y0, y1) = inner;

        if (x0 == 0 && y0 == 0 && x1 == width - 1 && y1 == height - 1) return null;
        return new PixelRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private readonly ref struct Image(ReadOnlySpan<byte> pixels, int width)
    {
        private readonly ReadOnlySpan<byte> _pixels = pixels;

        /// <summary>Пиксель <paramref name="i"/> строки (или столбца) <paramref name="index"/>.</summary>
        public Color At(int index, int i, bool horizontal)
        {
            var offset = (horizontal ? index * width + i : i * width + index) * 4;
            return new Color(_pixels[offset], _pixels[offset + 1], _pixels[offset + 2]);
        }

        /// <summary>Линия почти вся цвета <paramref name="color"/>.</summary>
        public bool Matches(int index, int from, int to, bool horizontal, Color color)
        {
            var count = to - from + 1;
            var allowed = count - (int)Math.Ceiling(count * MinBarShare);
            var off = 0;
            for (var i = from; i <= to; i++)
            {
                if (!At(index, i, horizontal).Near(color, Tolerance) && ++off > allowed) return false;
            }
            return true;
        }

        /// <summary>Цвет крайней линии, если она почти вся одного цвета; null — у края картинка.</summary>
        public Color? Edge(int index, int from, int to, bool horizontal)
        {
            long r = 0, g = 0, b = 0;
            for (var i = from; i <= to; i++)
            {
                var p = At(index, i, horizontal);
                r += p.R;
                g += p.G;
                b += p.B;
            }
            var count = to - from + 1;
            var mean = new Color((int)(r / count), (int)(g / count), (int)(b / count));
            return Matches(index, from, to, horizontal, mean) ? mean : null;
        }
    }

    /// <summary>Поля с двух концов стороны <paramref name="size"/>: линии поперёк — от <paramref name="from"/> до <paramref name="to"/>.</summary>
    private static (int Start, int End)? Bars(Image image, int size, int from, int to, bool horizontal)
    {
        if (image.Edge(0, from, to, horizontal) is not { } first || image.Edge(size - 1, from, to, horizontal) is not { } last
            || !first.Near(last, MaxSidesDifference)) return null;
        int start = 0, end = size - 1;
        while (start < end && image.Matches(start, from, to, horizontal, first)) start++;
        while (end > start && image.Matches(end, from, to, horizontal, last)) end--;
        var tail = size - 1 - end;
        if (start + tail < size * MinBars || Math.Abs(start - tail) > size * MaxAsymmetry || size - start - tail < size * MinContent) return null;
        return (start, end);
    }

    /// <summary>Рамка одного цвета со всех четырёх сторон прямоугольника: новые границы или null.</summary>
    private static (int X0, int X1, int Y0, int Y1)? Ring(Image image, int x0, int x1, int y0, int y1)
    {
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w <= 8 || h <= 8 || image.Edge(y0, x0, x1, horizontal: true) is not { } color) return null;
        int top = y0, bottom = y1, left = x0, right = x1;
        while (top < y1 && image.Matches(top, x0, x1, horizontal: true, color)) top++;
        while (bottom > top && image.Matches(bottom, x0, x1, horizontal: true, color)) bottom--;
        while (left < x1 && image.Matches(left, y0, y1, horizontal: false, color)) left++;
        while (right > left && image.Matches(right, y0, y1, horizontal: false, color)) right--;
        int[] vertical = [top - y0, y1 - bottom], across = [left - x0, x1 - right];
        if (vertical.Concat(across).Any(side => side < 1) || vertical.Any(side => side > h * MaxRing) || across.Any(side => side > w * MaxRing)) return null;
        // Ещё полпроцента: граница рамки у JPEG размыта, иначе по краю остаётся тёмный волосок
        var blend = Math.Max(1, (int)Math.Ceiling(Math.Min(w, h) * 0.005));
        if (right - left <= 2 * blend || bottom - top <= 2 * blend) return null;
        return (left + blend, right - blend, top + blend, bottom - blend);
    }
}
