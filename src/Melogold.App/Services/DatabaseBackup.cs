using Melogold.Core.Data;

namespace Melogold.App.Services;

/// <summary>
/// «Резервное копирование» и «Восстановить» (Android <c>DatabaseSettings</c>): база — в файл и из файла. В копию не
/// входят настройки (они в <c>settings.json</c>) и кэш — найденные в сети тексты. Восстановленная база встаёт на место
/// при следующем запуске: пока приложение работает, файл базы занят.
/// </summary>
public static class DatabaseBackup
{
    private static string Pending => AppPaths.Database + ".restore";

    /// <summary>Имя файла копии, как на Android: <c>Melogold_backup_ггггММддЧЧммсс.db</c>.</summary>
    public static string SuggestedName => $"Melogold_backup_{DateTime.Now:yyyyMMddHHmmss}";

    public static void Export(LibraryDatabase database, string target) => LibraryBackup.Export(database, target);

    public static bool IsValid(string path) => LibraryBackup.IsBackup(path);

    /// <summary>Восстановить из <paramref name="source"/> при следующем запуске.</summary>
    public static void Schedule(string source) => File.Copy(source, Pending, overwrite: true);

    /// <summary>
    /// При запуске, до открытия базы: отложенное восстановление встаёт на место. Прежний процесс мог ещё не отпустить
    /// файл — несколько попыток.
    /// </summary>
    public static void ApplyPending()
    {
        if (!File.Exists(Pending)) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                foreach (var file in new[] { AppPaths.Database + "-wal", AppPaths.Database + "-shm" }) File.Delete(file);
                File.Move(Pending, AppPaths.Database, overwrite: true);
                Log.Info("Database restored from a backup");
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(250);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Database restore failed", e);
                return;
            }
        }
    }
}
