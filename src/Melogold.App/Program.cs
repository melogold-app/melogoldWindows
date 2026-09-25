using System.Runtime.InteropServices;
using Melogold.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Melogold.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Для проверки перевода: MELOGOLD_LANG=ru-RU или en-US (обычно язык берётся из системы)
        if (Environment.GetEnvironmentVariable("MELOGOLD_LANG") is { Length: > 0 } language)
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(language);
        }

        // Один экземпляр: второй запуск (в том числе ссылка melogold://) передаётся первому и завершается
        var instance = AppInstance.FindOrRegisterForKey("Melogold.Main");
        if (!instance.IsCurrent)
        {
            RedirectActivation(instance);
            return 0;
        }
        instance.Activated += (_, e) => App.Current?.OnRedirectedActivation(e);

        Microsoft.UI.Xaml.Application.Start(startup =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    /// <summary>Передаёт активацию первому экземпляру; ждёт, пока она дойдёт, не блокируя STA-поток сообщений.</summary>
    private static void RedirectActivation(AppInstance target)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            target.RedirectActivationToAsync(activation).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        CloseHandle(done);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);
}
