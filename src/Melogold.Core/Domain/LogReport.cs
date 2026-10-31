using System.Globalization;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>
/// «Сохранить журнал…» (tasks/0039): журнал одним текстовым файлом, чтобы его можно было приложить к сообщению о
/// проблеме, а не искать в папке три файла. Шапка — версия, Windows, архитектура и время с поясом; дальше разделы, как у
/// Android (<c>LogFiles.export</c>): падение, прошлый запуск, этот запуск. Нет файла — нет и раздела. Файлы пишет
/// <c>Log</c> приложения: имена — отсюда, чтобы журнал и сборка его файлом не разошлись.
/// </summary>
public static class LogReport
{
    public const string CurrentFile = "current.log";

    /// <summary>Прошлый запуск; если этот перерос 2 МБ — его начало (<c>Log</c> переносит его сюда же).</summary>
    public const string PreviousFile = "previous.log";

    public const string CrashFile = "last-crash.txt";

    private static readonly (string File, string Title)[] Sections =
        [(CrashFile, "Падение"), (PreviousFile, "Прошлый запуск"), (CurrentFile, "Этот запуск")];

    /// <summary>Имя по умолчанию, как у Android: <c>melogold-log-ггггММдд-ЧЧмм</c>; <c>.txt</c> добавляет диалог по типу файла.</summary>
    public static string SuggestedName(DateTimeOffset now) => $"melogold-log-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Шапка: «Melogold 0.1.20», время в формате строк журнала и с поясом (по нему строки журнала сводятся с часами
    /// пользователя), Windows и архитектура.
    /// </summary>
    public static string Header(string version, string windows, string architecture, DateTimeOffset now) =>
        $"Melogold {version}\n" +
        $"Время: {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}\n" +
        $"Windows: {windows}\n" +
        $"Архитектура: {architecture}\n";

    /// <summary>Шапка и разделы из папки журнала по порядку; каждый — «===== Название (файл) =====» и текст файла.</summary>
    public static string Build(string header, string directory)
    {
        var text = new StringBuilder(header);
        foreach (var (file, title) in Sections)
        {
            if (Read(Path.Combine(directory, file)) is not { } content) continue;
            text.Append($"\n===== {title} ({file}) =====\n").Append(content);
        }
        return text.ToString();
    }

    /// <summary>
    /// Текст файла, который <c>Log</c> держит открытым на запись; нет файла — <c>null</c>. <c>Delete</c> — чтобы перенос
    /// <c>current.log</c> на 2 МБ не упал, пока файл читается здесь: иначе журнал замолчал бы до конца запуска.
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
