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

    public static string Account => Path.Combine(DataDirectory, "account.dat");

    public static string Logs => Path.Combine(DataDirectory, "logs");

        public static string Cache => Path.Combine(DataDirectory, "cache");

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
