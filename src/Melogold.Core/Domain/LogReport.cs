using System.Globalization;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>
/// «Сохранить журнал…» (tasks/0039, файлы — 0040): журнал одним текстовым файлом, чтобы его можно было приложить к
/// сообщению о проблеме, а не искать файлы в папке. Шапка — версия, Windows, архитектура, время с поясом и срок
/// хранения; дальше падение, если оно есть, и все журналы папки от старых к новым (<see cref="LogFiles.Logs"/>),
/// одинаково на всех клиентах.
/// </summary>
public static class LogReport
{
    /// <summary>Имя по умолчанию, как у Android: <c>melogold-log-ггггММдд-ЧЧмм</c>; <c>.txt</c> добавляет диалог по типу файла.</summary>
    public static string SuggestedName(DateTimeOffset now) => $"melogold-log-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Шапка: «Melogold 0.1.20», время в формате строк журнала и с поясом (по нему строки журнала сводятся с часами
    /// пользователя), Windows, архитектура и «Журнал: хранить 7 дн.» — сколько старого может быть в файле.
    /// </summary>
    public static string Header(string version, string windows, string architecture, int retentionDays, DateTimeOffset now) =>
        $"Melogold {version}\n" +
        $"Время: {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}\n" +
        $"Windows: {windows}\n" +
        $"Архитектура: {architecture}\n" +
        $"Журнал: хранить {retentionDays} дн.\n";

    /// <summary>
    /// Шапка, «===== Падение (last-crash.txt) =====», потом каждый журнал — «===== melogold-20261031-134500.log =====» и
    /// его текст. Нет падения или папки — нет и раздела.
    /// </summary>
    public static string Build(string header, string directory)
    {
        var text = new StringBuilder(header);
        if (Read(Path.Combine(directory, LogFiles.CrashFile)) is { } crash) text.Append($"\n===== Падение ({LogFiles.CrashFile}) =====\n").Append(crash);
        foreach (var log in LogFiles.Logs(directory))
        {
            if (Read(log.FullName) is not { } content) continue;
            text.Append($"\n===== {log.Name} =====\n").Append(content);
        }
        return text.ToString();
    }

    /// <summary>
    /// Текст файла, который <c>Log</c> может держать открытым на запись; нет файла (уже убран) — <c>null</c>.
    /// <c>Delete</c> — чтобы уборка, если она начнётся, пока файл читается здесь, не пропускала его.
    /// </summary>
    private static string? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
