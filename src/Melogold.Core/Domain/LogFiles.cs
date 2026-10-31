using System.Globalization;

namespace Melogold.Core.Domain;

/// <summary>
/// Папка журнала (tasks/0040, одинаково на всех клиентах): каждый запуск пишет в <c>melogold-ГГГГММДД-ЧЧММСС.log</c>
/// (местное время начала файла), новый файл — на 2 МБ и со сменой даты; падение — <c>last-crash.txt</c>. Хранится
/// выбранный срок (день, 3 дня, неделя, месяц) и не больше 50 МБ вместе. Файлы <c>current.log</c> и <c>previous.log</c>
/// прежних версий — тоже <c>*.log</c>: уходят по сроку сами.
/// </summary>
public static class LogFiles
{
    public const string CrashFile = "last-crash.txt";

    public const long MaxFileBytes = 2 * 1024 * 1024;

    public const long MaxTotalBytes = 50 * 1024 * 1024;

    /// <summary>«Хранить»: День · 3 дня · Неделя · Месяц — в этом порядке и в настройках.</summary>
    public static readonly int[] RetentionDays = [1, 3, 7, 30];

    public const int DefaultRetentionDays = 7;

    /// <summary>Срок из настроек; чужое значение (правка руками, другая версия) — неделя.</summary>
    public static int Retention(int days) => Array.IndexOf(RetentionDays, days) >= 0 ? days : DefaultRetentionDays;

    public static string FileName(DateTime start) => $"melogold-{start.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";

    /// <summary>Новый файл, когда текущий дорос до 2 МБ или сменилась дата.</summary>
    public static bool NeedsNewFile(DateTime started, long length, DateTime now) => length >= MaxFileBytes || now.Date != started.Date;

    /// <summary>Журналы папки (<c>*.log</c>, без падения) от старых к новым по времени изменения.</summary>
    public static IReadOnlyList<FileInfo> Logs(string directory) =>
        Files(directory).Where(f => f.Name != CrashFile).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Уборка — при старте, на каждом новом файле и при смене срока: <c>*.log</c> и <c>last-crash.txt</c> старше срока
    /// по времени изменения, потом самые старые, пока всё вместе больше 50 МБ. Файл, в который идёт запись
    /// (<paramref name="writing"/>), остаётся. Не удалился (открыт другой программой) — остаётся до следующей уборки.
    /// Не бросает: её зовёт журнал, в том числе на старте приложения.
    /// </summary>
    public static void Clean(string directory, DateTimeOffset now, int retentionDays, string? writing)
    {
        var limit = now.UtcDateTime - TimeSpan.FromDays(retentionDays);
        var current = writing is null ? null : Path.GetFullPath(writing);
        bool Writing(FileInfo file) => string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase);
        try
        {
            // Размер — заранее: после Delete у FileInfo его уже не спросить
            var kept = new List<(FileInfo File, long Length)>();
            foreach (var file in Files(directory).OrderBy(f => f.LastWriteTimeUtc))
            {
                var length = file.Length;
                if (Writing(file) || file.LastWriteTimeUtc >= limit || !TryDelete(file)) kept.Add((file, length));
            }
            var total = kept.Sum(f => f.Length);
            foreach (var (file, length) in kept)
            {
                if (total <= MaxTotalBytes) break;
                if (!Writing(file) && TryDelete(file)) total -= length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Журнал выключили: всё из папки сразу, вместе с файлом этого запуска (его уже закрыли). Не бросает.</summary>
    public static void DeleteAll(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles().ToList()) TryDelete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<FileInfo> Files(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles().Where(f => f.Name == CrashFile || string.Equals(f.Extension, ".log", StringComparison.OrdinalIgnoreCase))
            : [];

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
