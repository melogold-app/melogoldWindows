using System.Globalization;
using System.Runtime.InteropServices;

namespace Melogold.App.Services;

/// <summary>
/// Место и размер окна между запусками, как у приложений Windows: <c>GetWindowPlacement</c> при закрытии,
/// <c>SetWindowPlacement</c> при запуске. Хранится обычное (не развёрнутое) положение и то, было ли окно развёрнуто —
/// развёрнутое окно после «Восстановить» возвращается туда, где было. Если того монитора больше нет, место не
/// восстанавливается: окно встаёт по умолчанию, а не за краем экрана.
/// </summary>
public static class WindowPlacement
{
    private const int SwHide = 0, SwShowMinimized = 2, SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;
    private const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public Rect Normal;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    /// <summary>Как стоит окно сейчас: «l,t,r,b,max»; null — не узнать.</summary>
    public static string? Capture(IntPtr hwnd)
    {
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return null;
        // Свёрнутое вернётся таким, каким было до сворачивания
        var maximized = placement.ShowCmd == SwShowMaximized || (placement.ShowCmd == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0);
        var r = placement.Normal;
        return string.Join(',', new[] { r.Left, r.Top, r.Right, r.Bottom, maximized ? 1 : 0 }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Поставить окно, как сохранено. Обычное окно остаётся скрытым до <c>Activate</c> — показывается сразу на своём
    /// месте, без прыжка; развёрнутое показывается развёрнутым. false — сохранённого нет, оно битое или вне экранов.
    /// </summary>
    public static bool Restore(IntPtr hwnd, string? saved)
    {
        if (saved?.Split(',') is not { Length: 5 } parts) return false;
        var values = new int[5];
        for (var i = 0; i < 5; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return false;
        var rect = new Rect { Left = values[0], Top = values[1], Right = values[2], Bottom = values[3] };
        if (rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100 || MonitorFromRect(ref rect, MonitorDefaultToNull) == IntPtr.Zero) return false;
        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCmd = values[4] == 1 ? SwShowMaximized : SwHide,
            Normal = rect,
        };
        return SetWindowPlacement(hwnd, ref placement);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Левый край места правее всех мониторов: там окно тихого режима (<see cref="Core.Domain.QuietMode"/>) рисуется, но
    /// его не видно.
    /// </summary>
    public static int OffScreenX() => GetSystemMetrics(76) + GetSystemMetrics(78) + 200;   // SM_XVIRTUALSCREEN + SM_CXVIRTUALSCREEN

    /// <summary>Точка левого верхнего угла на каком-нибудь мониторе (для окна без своего размера — мини-плеера).</summary>
    public static bool OnScreen(int x, int y, int width, int height)
    {
        var rect = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        return MonitorFromRect(ref rect, MonitorDefaultToNull) != IntPtr.Zero;
    }
}
