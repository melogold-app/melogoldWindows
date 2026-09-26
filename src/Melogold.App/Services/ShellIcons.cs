using System.Runtime.InteropServices;

namespace Melogold.App.Services;

/// <summary>
/// Значок приложения в проводнике и на панели задач. Windows держит картинку закреплённого ярлыка в своём кэше значков
/// и после обновления её не перечитывает: пользователь видел старый квадратный значок, хотя в новой версии он
/// скруглённый (2026-09-26). При первом запуске новой версии оболочке сообщается, что значки изменились, — так же
/// делает установщик в конце (<c>ChangesAssociations=yes</c>).
/// </summary>
public static class ShellIcons
{
    private const int ShcneUpdateItem = 0x00002000, ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000, ShcnfPathW = 0x0005, ShcnfFlushNoWait = 0x3000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Сбросить значок исполняемого файла и заставить оболочку перечитать значки (ярлыки, панель задач).</summary>
    public static void Refresh()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var path = Marshal.StringToHGlobalUni(exe);
                try
                {
                    SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlushNoWait, path, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(path);
                }
            }
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlushNoWait, IntPtr.Zero, IntPtr.Zero);
            Log.Info("Shell icons refreshed after an update");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Shell icons not refreshed", e);
        }
    }
}
