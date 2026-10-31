using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Папка журнала (tasks/0040): имя файла по времени начала, новый файл на 2 МБ и со сменой даты, срок «Хранить»,
/// уборка по сроку и до 50 МБ без файла, в который идёт запись, и «выключили — удалить всё».
/// </summary>
public sealed class LogFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 13, 45, 0, TimeSpan.FromHours(3));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-logs-test-{Guid.NewGuid():N}");

    public LogFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private string Make(string file, DateTimeOffset modified, long length = 10)
    {
        var path = Path.Combine(_directory, file);
        using (var stream = File.Create(path)) stream.SetLength(length);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private string[] Left() => Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void TheFileIsNamedByItsLocalStartToTheSecond()
    {
        Assert.Equal("melogold-20261031-134500.log", LogFiles.FileName(new DateTime(2026, 10, 31, 13, 45, 0)));
        Assert.Equal("melogold-20260105-090807.log", LogFiles.FileName(new DateTime(2026, 1, 5, 9, 8, 7)));
    }

    [Fact]
    public void ANewFileStartsAtTwoMegabytesOrOnANewDate()
    {
        var started = new DateTime(2026, 10, 31, 23, 59, 0);
        Assert.False(LogFiles.NeedsNewFile(started, LogFiles.MaxFileBytes - 1, started.AddSeconds(30)));
        Assert.True(LogFiles.NeedsNewFile(started, LogFiles.MaxFileBytes, started.AddSeconds(30)));
        Assert.True(LogFiles.NeedsNewFile(started, 10, started.AddMinutes(1)));
    }

    [Fact]
    public void TheRetentionIsADayThreeDaysAWeekOrAMonthAndAWeekOtherwise()
    {
        Assert.Equal([1, 3, 7, 30], LogFiles.RetentionDays);
        foreach (var days in LogFiles.RetentionDays) Assert.Equal(days, LogFiles.Retention(days));
        Assert.Equal(7, LogFiles.Retention(0));
        Assert.Equal(7, LogFiles.Retention(14));
        Assert.Equal(7, LogFiles.Retention(-1));
    }

    /// <summary>Неделя: старше 7 дней уходят и журналы, и падение, и <c>current.log</c> прошлой версии; чужое и пишущийся — остаются.</summary>
    [Fact]
    public void LogsAndTheCrashOlderThanTheRetentionAreRemoved()
    {
        Make("melogold-20261023-100000.log", Now.AddDays(-8));
        Make("current.log", Now.AddDays(-10));
        Make(LogFiles.CrashFile, Now.AddDays(-8));
        Make("previous.log", Now.AddDays(-6));
        Make("melogold-20261031-120000.log", Now.AddHours(-1));
        Make("notes.txt", Now.AddDays(-30));
        // Часы перевели или файл старый, но в него идёт запись — его не трогать
        var writing = Make("melogold-20261011-080000.log", Now.AddDays(-20));

        LogFiles.Clean(_directory, Now, 7, writing);

        Assert.Equal(["melogold-20261011-080000.log", "melogold-20261031-120000.log", "notes.txt", "previous.log"], Left());
    }

    [Fact]
    public void ADayKeepsOnlyTheLastTwentyFourHours()
    {
        Make("melogold-20261030-100000.log", Now.AddDays(-1).AddMinutes(-1));
        Make("melogold-20261030-150000.log", Now.AddHours(-23));

        LogFiles.Clean(_directory, Now, 1, null);

        Assert.Equal(["melogold-20261030-150000.log"], Left());
    }

    /// <summary>Больше 50 МБ — уходят самые старые, пока не станет не больше 50; пишущийся остаётся, хоть он и старше всех.</summary>
    [Fact]
    public void OverFiftyMegabytesTheOldestGoFirstButNotTheOneBeingWritten()
    {
        const long twenty = 20 * 1024 * 1024;
        var writing = Make("melogold-20261031-100000.log", Now.AddHours(-3), twenty);
        Make("melogold-20261031-110000.log", Now.AddHours(-2), twenty);
        Make("melogold-20261031-120000.log", Now.AddHours(-1), twenty);
        Make(LogFiles.CrashFile, Now.AddMinutes(-1), 1024);

        LogFiles.Clean(_directory, Now, 30, writing);

        Assert.Equal(["last-crash.txt", "melogold-20261031-100000.log", "melogold-20261031-120000.log"], Left());
    }

    [Fact]
    public void FiftyMegabytesExactlyStay()
    {
        Make("melogold-20261031-100000.log", Now.AddHours(-2), 25 * 1024 * 1024);
        Make("melogold-20261031-110000.log", Now.AddHours(-1), 25 * 1024 * 1024);

        LogFiles.Clean(_directory, Now, 30, null);

        Assert.Equal(2, Left().Length);
    }

    [Fact]
    public void TurningTheLogOffRemovesEverythingInTheFolder()
    {
        Make("melogold-20261031-134500.log", Now);
        Make(LogFiles.CrashFile, Now);
        Make("previous.log", Now.AddDays(-1));
        Make("notes.txt", Now);

        LogFiles.DeleteAll(_directory);
        LogFiles.DeleteAll(Path.Combine(_directory, "missing"));

        Assert.Empty(Left());
    }

    [Fact]
    public void TheLogsAreListedFromOldestToNewestWithoutTheCrash()
    {
        Make("melogold-20261031-120000.log", Now.AddHours(-1));
        Make("previous.log", Now.AddDays(-2));
        Make(LogFiles.CrashFile, Now.AddDays(-3));

        Assert.Equal(["previous.log", "melogold-20261031-120000.log"], LogFiles.Logs(_directory).Select(f => f.Name));
    }
}
