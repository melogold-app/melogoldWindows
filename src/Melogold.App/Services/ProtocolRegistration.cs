using Microsoft.Win32;

namespace Melogold.App.Services;

/// <summary>
/// Протокол <c>melogold://</c> в <c>HKCU\Software\Classes\melogold</c> (API §7.2). Установщик пишет то же; приложение
/// подправляет путь, если его запустили из другой папки (сборка разработчика, перенос).
/// </summary>
public static class ProtocolRegistration
{
    private const string Key = @"Software\Classes\melogold";

    public static void Ensure()
    {
        try
        {
            var command = $"\"{AppInfo.ExecutablePath}\" \"%1\"";
            using var existing = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command");
            if (existing?.GetValue(null) as string == command) return;

            using var root = Registry.CurrentUser.CreateSubKey(Key);
            root.SetValue(null, "URL:Melogold");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{AppInfo.ExecutablePath}\",0");
            using var open = root.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
            Log.Info("melogold:// registered");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("melogold:// not registered", e);
        }
    }
}
