namespace Melogold.Core.Artwork;

/// <summary>Положение одного слоя фона: центр, сторона квадрата, поворот и непрозрачность.</summary>
public readonly record struct BackdropLayer(double CenterX, double CenterY, double Side, double Degrees, double Opacity);

/// <summary>
/// Движение фона «Сейчас играет» (tasks/0034 §1): два квадрата размытой обложки, вращающиеся навстречу. Формулы — из
/// задания, одни на всех платформах; <c>t</c> — секунды часов анимации, <c>w</c>, <c>h</c> — размер области.
/// </summary>
public static class BackdropMotion
{
    /// <summary>Слой 1: сторона 1,6 · max(w, h) вокруг середины, поворот t · 2,4°, непрозрачность 1.</summary>
    public static BackdropLayer First(double t, double w, double h) =>
        new(w / 2, h / 2, 1.6 * Math.Max(w, h), t * 2.4, 1);

    /// <summary>Слой 2: сторона 1,15 · max(w, h), центр плывёт, поворот 90° − t · 3,6°, непрозрачность 0,7.</summary>
    public static BackdropLayer Second(double t, double w, double h) =>
        new(w / 2 + Math.Sin(t / 17) * 0.18 * w, h / 2 + Math.Cos(t / 23) * 0.15 * h, 1.15 * Math.Max(w, h), 90 - t * 3.6, 0.7);

    /// <summary>Радиус размытия слоёв, логических пикселей (гауссово, как <c>GtkSnapshot.push_blur</c>: σ = радиус / 2).</summary>
    public const double BlurRadius = 32;

    /// <summary>Виньетка: до этой доли радиуса прозрачная, дальше темнеет до <see cref="VignetteEdge"/>.</summary>
    public const double VignetteStart = 0.55;

    /// <summary>Непрозрачность чёрного виньетки на краю (радиусы 0,75 · w и 0,75 · h).</summary>
    public const double VignetteEdge = 0.30;

    public const double VignetteRadius = 0.75;

    /// <summary>Перекрёстное затухание старой обложки при смене трека, мс.</summary>
    public const int CrossfadeMs = 400;
}

/// <summary>
/// Часы анимации фона: идут, только пока трек играет, окно видно, «Живой фон» включён и в системе не выключены анимации;
/// иначе стоят. Пауза время не сбрасывает, смена трека — тоже.
/// </summary>
public sealed class BackdropClock
{
    /// <summary>Секунды анимации.</summary>
    public double Seconds { get; private set; }

    /// <summary>Часы должны идти.</summary>
    public static bool ShouldRun(bool playing, bool windowVisible, bool liveBackground, bool animationsEnabled) =>
        playing && IsLive(windowVisible, liveBackground, animationsEnabled);

    /// <summary>
    /// Фон может двигаться (tasks/0035 §6): окно видно, «Живой фон» включён, анимации системы не выключены. Двигается, если ещё и
    /// играет трек; на паузе шейдер гасит силы и замирает. Иначе — замерший кадр.
    /// </summary>
    public static bool IsLive(bool windowVisible, bool liveBackground, bool animationsEnabled) =>
        windowVisible && liveBackground && animationsEnabled;

    /// <summary>Прошло <paramref name="elapsed"/>; <paramref name="running"/> — см. <see cref="ShouldRun"/>. Возвращает новое время.</summary>
    public double Advance(TimeSpan elapsed, bool running)
    {
        if (running && elapsed > TimeSpan.Zero) Seconds += elapsed.TotalSeconds;
        return Seconds;
    }
}

/// <summary>Размеры «Сейчас играет» от области целиком (tasks/0034 §3).</summary>
public static class NowPlayingMetrics
{
    /// <summary>
    /// Размер основной строки текста песни: <c>clamp(0,042 · H, 18, 64)</c>, где <paramref name="height"/> — высота области
    /// «Сейчас играет» целиком, а не колонки текста: в окне любой ширины видно одно и то же число строк.
    /// </summary>
    public static double LyricsFontSize(double height) => Math.Clamp(0.042 * height, 18, 64);

    /// <summary>
    /// Предельная ширина текста песни в широкой раскладке: около 28 знаков (знак — примерно 0,55 размера шрифта) и поля
    /// строки (16 с каждой стороны при размере 28, дальше в том же масштабе).
    /// </summary>
    public static double LyricsMaxWidth(double fontSize) => 28 * 0.55 * fontSize + 32 * fontSize / 28;

    /// <summary>Сторона обложки в широкой раскладке: <c>min(0,50 · H, 0,38 · W)</c>.</summary>
    public static double WideCoverSide(double width, double height) => Math.Min(0.50 * height, 0.38 * width);
}

/// <summary>
/// Затухание строк текста песни у краёв области прокрутки (tasks/0034): как маска у Linux — 24 px сверху и 72 снизу, линейно;
/// у Windows на строку целиком, по её положению: <c>clamp(верх / 24, 0, 1)</c> у верха и <c>clamp((H − низ) / 72, 0, 1)</c> у низа.
/// </summary>
public static class LyricsEdgeFade
{
    /// <summary>Высота полосы затухания сверху, px.</summary>
    public const double Top = 24;

    /// <summary>Высота полосы затухания снизу, px.</summary>
    public const double Bottom = 72;

    /// <summary>
    /// Прозрачность строки: <paramref name="top"/> и <paramref name="bottom"/> — её верх и низ в области высотой
    /// <paramref name="height"/> (0 — верх области). Строка целиком в середине — 1, ушла за край — 0.
    /// </summary>
    public static double Opacity(double top, double bottom, double height) =>
        Math.Min(Math.Clamp(top / Top, 0, 1), Math.Clamp((height - bottom) / Bottom, 0, 1));
}
