namespace Melogold.App.Services;

/// <summary>
/// Где приложение хранит данные: база, кэш и логи — в <c>%LOCALAPPDATA%\Melogold</c> (docs/PROMPT.md §3). Программа
/// ставится отдельно, в <c>%LOCALAPPDATA%\Programs\Melogold</c>, и её удаление данные не трогает.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("MELOGOLD_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melogold");

    public static string Database => Path.Combine(DataDirectory, "library.db");

    public static string Settings => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Сессия на сервере Melogold, зашифрованная DPAPI для пользователя Windows.</summary>
    public static string Account => Path.Combine(DataDirectory, "account.dat");

    /// <summary>Соль установки для hwid (API §1.6): случайная, создаётся один раз.</summary>
    public static string InstallSalt => Path.Combine(DataDirectory, "install-salt");

    public static string Logs => Path.Combine(DataDirectory, "logs");

    public static string Cache => Path.Combine(DataDirectory, "cache");

    /// <summary>Скачанные треки (tasks/0003): не кэш — ни лимит, ни «Очистить кэш» их не трогают.</summary>
    public static string Downloads => Path.Combine(DataDirectory, "downloads");

    public static string Updates => Path.Combine(DataDirectory, "updates");

    public static string Backups => Path.Combine(DataDirectory, "backups");

    public static void Ensure()
    {
        foreach (var directory in new[] { DataDirectory, Logs, Cache, Backups })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
