namespace Melogold.App.Services;

/// <summary>
/// Где приложение хранит данные. Не в папке установки Velopack (<c>%LocalAppData%\Melogold</c>): её заменяет каждое
/// обновление и удаляет удаление программы. Библиотека живёт отдельно, в <c>%LocalAppData%\Melogold Data</c>.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("MELOGOLD_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melogold Data");

    public static string Database => Path.Combine(DataDirectory, "library.db");

    public static string Settings => Path.Combine(DataDirectory, "settings.json");

    public static string Account => Path.Combine(DataDirectory, "account.dat");

    public static string Logs => Path.Combine(DataDirectory, "logs");

    public static string Tools => Path.Combine(DataDirectory, "tools");

    public static string Artwork => Path.Combine(DataDirectory, "artwork");

    public static string Backups => Path.Combine(DataDirectory, "backups");

    public static void Ensure()
    {
        foreach (var directory in new[] { DataDirectory, Logs, Tools, Backups })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
