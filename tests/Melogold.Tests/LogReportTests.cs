using System.Text;
using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// «Сохранить журнал…» (tasks/0039, файлы — 0040): один файл — шапка, падение, если оно есть, и все журналы папки от
/// старых к новым по времени изменения; журнал, открытый на запись, читается.
/// </summary>
public sealed class LogReportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 3, 7, 45, TimeSpan.FromHours(3));

    private static readonly string Header = LogReport.Header("0.1.20", "11 24H2, сборка 26100.4652", "x64", 7, Now);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-log-test-{Guid.NewGuid():N}");

    public LogReportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private void Write(string file, string text, DateTimeOffset modified)
    {
        var path = Path.Combine(_directory, file);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    [Fact]
    public void TheHeaderHasTheVersionTimeWithItsZoneWindowsArchitectureAndRetention()
    {
        Assert.Equal("Melogold 0.1.20\nВремя: 2026-10-31 03:07:45 +03:00\nWindows: 11 24H2, сборка 26100.4652\nАрхитектура: x64\nЖурнал: хранить 7 дн.\n", Header);
    }

    [Fact]
    public void TheSuggestedNameIsTheLocalDateAndTime()
    {
        Assert.Equal("melogold-log-20261031-0307", LogReport.SuggestedName(Now));
    }

    /// <summary>
    /// По времени изменения, а не по имени: <c>previous.log</c> прошлой версии — старше всех, хоть по имени и последний;
    /// падение — первым, хоть оно и новее.
    /// </summary>
    [Fact]
    public void TheCrashGoesFirstThenTheLogsFromOldestToNewest()
    {
        Write("melogold-20261031-030000.log", "сейчас\n", Now.AddMinutes(-1));
        Write("previous.log", "прошлая версия\n", Now.AddDays(-2));
        Write("melogold-20261030-210000.log", "вчера\n", Now.AddHours(-6));
        Write(LogFiles.CrashFile, "2026-10-31T02:59:59.0000000+03:00\nMelogold 0.1.20\nUI\n\nSystem.InvalidOperationException: падение", Now.AddMinutes(-8));

        Assert.Equal(Header +
            "\n===== Падение (last-crash.txt) =====\n2026-10-31T02:59:59.0000000+03:00\nMelogold 0.1.20\nUI\n\nSystem.InvalidOperationException: падение" +
            "\n===== previous.log =====\nпрошлая версия\n" +
            "\n===== melogold-20261030-210000.log =====\nвчера\n" +
            "\n===== melogold-20261031-030000.log =====\nсейчас\n",
            LogReport.Build(Header, _directory));
    }

    [Fact]
    public void WithoutACrashThereIsNoCrashSection()
    {
        Write("melogold-20261031-030000.log", "сейчас\n", Now);

        var text = LogReport.Build(Header, _directory);

        Assert.DoesNotContain("Падение", text);
        Assert.Equal(Header + "\n===== melogold-20261031-030000.log =====\nсейчас\n", text);
    }

    [Fact]
    public void OtherFilesOfTheFolderAreNotInTheReport()
    {
        Write("melogold-20261031-030000.log", "сейчас\n", Now);
        Write("notes.txt", "чужое", Now);

        Assert.DoesNotContain("чужое", LogReport.Build(Header, _directory));
    }

    [Fact]
    public void WithoutTheLogFolderOnlyTheHeaderIsLeft()
    {
        Assert.Equal(Header, LogReport.Build(Header, Path.Combine(_directory, "logs")));
    }

    /// <summary>Как <c>Log.Open</c>: файл открыт на запись с <c>FileShare.ReadWrite</c>, строки уходят без буфера.</summary>
    [Fact]
    public void TheLogBeingWrittenIsReadWhileItIsOpen()
    {
        using var writer = new StreamWriter(new FileStream(Path.Combine(_directory, "melogold-20261031-030745.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine("до нажатия");

        Assert.Contains("===== melogold-20261031-030745.log =====\nдо нажатия", LogReport.Build(Header, _directory));

        writer.WriteLine("после");
        Assert.Contains("после", LogReport.Build(Header, _directory));
    }
}
