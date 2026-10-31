using System.Globalization;
using System.Text;
using Melogold.Core.Domain;
using Microsoft.Win32;

namespace Melogold.App.Services;

/// <summary>
/// Журнал (docs/PROMPT.md §3, tasks/0040): <c>logs\melogold-ГГГГММДД-ЧЧММСС.log</c> на запуск, новый файл — на 2 МБ и со
/// сменой даты; последнее падение — отдельным файлом <c>last-crash.txt</c>. «Журнал» и «Хранить» — в настройках, смена
/// применяется сразу, без перезапуска: выключили — ничего не пишется и записанное удаляется, старое уходит по сроку и
/// сверх 50 МБ (<see cref="LogFiles"/>). Пароли и токены сюда не пишутся. «Сохранить журнал…» собирает всё одним файлом
/// (<see cref="Report"/>).
/// </summary>
public static class Log
{
    /// <summary>Строки до <see cref="Start"/>: настройки читаются раньше журнала, и пока неясно, включён ли он.</summary>
    private const int MaxEarlyLines = 200;

    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;
    private static DateTime _started;
    private static bool _enabled;
    private static int _retentionDays = LogFiles.DefaultRetentionDays;
    private static List<string>? _early = [];

    public static string CrashPath => Path.Combine(AppPaths.Logs, LogFiles.CrashFile);

    /// <summary>Журнал по настройкам; дальше «Журнал» и «Хранить» применяются, как только их сменили.</summary>
    public static void Start(SettingsStore settings)
    {
        Apply(settings.LogEnabled, settings.LogRetentionDays);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsStore.LogEnabled) or nameof(SettingsStore.LogRetentionDays)) Apply(settings.LogEnabled, settings.LogRetentionDays);
        };
    }

    public static void Info(string message) => Write("I", message);

    public static void Warn(string message, Exception? error = null) => Write("W", error is null ? message : $"{message}: {error.GetType().Name}: {error.Message}");

    public static void Error(string message, Exception? error = null) => Write("E", error is null ? message : $"{message}: {error}");

    private static void Apply(bool enabled, int retentionDays)
    {
        lock (Gate)
        {
            _retentionDays = retentionDays;
            var early = _early;
            _early = null;
            if (!enabled)
            {
                // Выключили: запись прекращается, всё записанное удаляется — и файл этого запуска
                _enabled = false;
                Close();
                LogFiles.DeleteAll(AppPaths.Logs);
                return;
            }
            if (_enabled && _writer is not null)
            {
                // Сменили срок: лишнее — сразу
                LogFiles.Clean(AppPaths.Logs, DateTimeOffset.Now, retentionDays, _path);
                return;
            }
            // Старт или включили снова — новый файл с первой строкой о версии и системе
            _enabled = true;
            Open(DateTime.Now);
            Append(Line("I", $"Melogold {AppInfo.Version} on Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"));
            foreach (var line in early ?? []) Append(line);
        }
    }

    private static void Write(string level, string message)
    {
        var line = Line(level, message);
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            if (_early is null) Append(line);
            else if (_early.Count < MaxEarlyLines) _early.Add(line);
        }
    }

    private static string Line(string level, string message) =>
        $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} [{Environment.CurrentManagedThreadId}] {message}";

    /// <summary>Строка в файл (под <see cref="Gate"/>); файл дорос до 2 МБ или сменилась дата — сначала новый.</summary>
    private static void Append(string line)
    {
        if (_writer is null) return;
        try
        {
            var now = DateTime.Now;
            if (LogFiles.NeedsNewFile(_started, _writer.BaseStream.Length, now))
            {
                Open(now);
                if (_writer is null) return;
            }
            _writer.WriteLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Новый файл со временем начала в имени и уборка: старое по сроку, всё вместе — не больше 50 МБ.</summary>
    private static void Open(DateTime now)
    {
        Close();
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var path = Path.Combine(AppPaths.Logs, LogFiles.FileName(now));
            // Та же секунда (включили снова, перезапуск) — дописывается, а не затирается
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            _path = path;
            _started = now;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        LogFiles.Clean(AppPaths.Logs, DateTimeOffset.Now, _retentionDays, _path);
    }

    private static void Close()
    {
        _writer?.Dispose();
        _writer = null;
        _path = null;
    }

    /// <summary>Падение: полный текст исключения в <c>last-crash.txt</c> и в журнал; журнал выключен — никуда.</summary>
    public static void Crash(Exception error, string source)
    {
        Error($"Crash ({source})", error);
        if (!_enabled) return;
        try
        {
            File.WriteAllText(CrashPath, $"{DateTimeOffset.Now:O}\nMelogold {AppInfo.Version}\n{source}\n\n{error}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// «Сохранить журнал…» (tasks/0039): шапка, падение и все журналы папки — одним текстом (<see cref="LogReport"/>).
    /// Строки до нажатия уже на диске: журнал пишется без буфера.
    /// </summary>
    public static string Report() =>
        LogReport.Build(LogReport.Header(AppInfo.Version, WindowsVersion(), ArchitectureText(), _retentionDays, DateTimeOffset.Now), AppPaths.Logs);

    /// <summary>
    /// «11 24H2, сборка 26100.4652»: выпуск и номер обновления — из реестра (в <c>OSVersion</c> их нет), не прочитались —
    /// только сборка. 11 или 10 — по сборке, как <c>WindowsDeviceIdentity</c>: <c>ProductName</c> и у Windows 11 пишет «10».
    /// </summary>
    private static string WindowsVersion()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Environment.OSVersion.Version.Build;
        string? release = null;
        var update = "";
        try
        {
            release = Registry.GetValue(key, "DisplayVersion", null) as string;
            if (Registry.GetValue(key, "UBR", null) is int ubr) update = $".{ubr}";
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException)
        {
        }
        var name = build >= 22000 ? "11" : "10";
        return string.IsNullOrEmpty(release) ? $"{name}, сборка {build}{update}" : $"{name} {release}, сборка {build}{update}";
    }

    /// <summary>Сборка приложения (<c>x64</c>, <c>arm64</c>); у системы другая (x64 на Windows для ARM) — и она.</summary>
    private static string ArchitectureText()
    {
        var system = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return system == AppInfo.Architecture ? AppInfo.Architecture : $"{AppInfo.Architecture}, Windows {system}";
    }
}
