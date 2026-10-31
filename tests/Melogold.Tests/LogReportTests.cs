using System.Text;
using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// «Сохранить журнал…» (tasks/0039): один файл — шапка, затем падение, прошлый запуск и этот; файла нет — нет и раздела;
/// <c>current.log</c> читается, пока журнал держит его открытым на запись.
/// </summary>
public sealed class LogReportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 3, 7, 45, TimeSpan.FromHours(3));

    private static readonly string Header = LogReport.Header("0.1.20", "11 24H2, сборка 26100.4652", "x64", Now);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-log-test-{Guid.NewGuid():N}");

    public LogReportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_directory, file), text);

    [Fact]
    public void TheHeaderHasTheVersionTimeWithItsZoneWindowsAndArchitecture()
    {
        Assert.Equal("Melogold 0.1.20\nВремя: 2026-10-31 03:07:45 +03:00\nWindows: 11 24H2, сборка 26100.4652\nАрхитектура: x64\n", Header);
    }

    [Fact]
    public void TheSuggestedNameIsTheLocalDateAndTime()
    {
        Assert.Equal("melogold-log-20261031-0307", LogReport.SuggestedName(Now));
    }

    [Fact]
    public void TheCrashThePreviousRunAndThisRunFollowTheHeaderInThatOrder()
    {
        Write(LogReport.CurrentFile, "2026-10-31 03:07:40.000 I [1] этот запуск\n");
        Write(LogReport.PreviousFile, "2026-10-30 23:00:00.000 I [1] прошлый запуск\n");
        Write(LogReport.CrashFile, "2026-10-30T23:59:59.0000000+03:00\nMelogold 0.1.20\nUI\n\nSystem.InvalidOperationException: падение");

        Assert.Equal(Header +
            "\n===== Падение (last-crash.txt) =====\n2026-10-30T23:59:59.0000000+03:00\nMelogold 0.1.20\nUI\n\nSystem.InvalidOperationException: падение" +
            "\n===== Прошлый запуск (previous.log) =====\n2026-10-30 23:00:00.000 I [1] прошлый запуск\n" +
            "\n===== Этот запуск (current.log) =====\n2026-10-31 03:07:40.000 I [1] этот запуск\n",
            LogReport.Build(Header, _directory));
    }

    [Fact]
    public void AMissingFileHasNoSection()
    {
        Write(LogReport.PreviousFile, "прошлый\n");
        Write(LogReport.CurrentFile, "этот\n");

        var text = LogReport.Build(Header, _directory);

        Assert.DoesNotContain("Падение", text);
        Assert.Equal(Header + "\n===== Прошлый запуск (previous.log) =====\nпрошлый\n\n===== Этот запуск (current.log) =====\nэтот\n", text);
    }

    [Fact]
    public void WithoutTheLogFolderOnlyTheHeaderIsLeft()
    {
        Assert.Equal(Header, LogReport.Build(Header, Path.Combine(_directory, "logs")));
    }

    /// <summary>Как <c>Log.Start</c>: файл открыт на запись с <c>FileShare.ReadWrite</c>, строки уходят без буфера.</summary>
    [Fact]
    public void TheCurrentLogIsReadWhileItIsOpenForWriting()
    {
        using var writer = new StreamWriter(new FileStream(Path.Combine(_directory, LogReport.CurrentFile), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine("до нажатия");

        Assert.Contains("===== Этот запуск (current.log) =====\nдо нажатия", LogReport.Build(Header, _directory));

        writer.WriteLine("после");
        Assert.Contains("после", LogReport.Build(Header, _directory));
    }
}
