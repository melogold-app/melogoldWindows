using System.Globalization;
using System.Text;
using Melogold.Core.Domain;
using Microsoft.Win32;

namespace Melogold.App.Services;

/// <summary>
/// Журнал (docs/PROMPT.md §3): <c>logs\current.log</c> до 2 МБ, при старте прежний переименовывается в
/// <c>previous.log</c>; последнее падение — отдельным файлом <c>last-crash.txt</c>. Пароли и токены сюда не пишутся.
/// «Сохранить журнал…» собирает всё это одним файлом (<see cref="Report"/>).
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;

    public static string CurrentPath => Path.Combine(AppPaths.Logs, LogReport.CurrentFile);

    public static string PreviousPath => Path.Combine(AppPaths.Logs, LogReport.PreviousFile);

    public static string CrashPath => Path.Combine(AppPaths.Logs, LogReport.CrashFile);

    public static void Start()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                if (File.Exists(CurrentPath)) File.Move(CurrentPath, PreviousPath, overwrite: true);
                _writer = new StreamWriter(new FileStream(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (IOException)
            {
                _writer = null;
            }
            catch (UnauthorizedAccessException)
            {
                _writer = null;
            }
        }
        Info($"Melogold {AppInfo.Version} on Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    public static void Info(string message) => Write("I", message);

    public static void Warn(string message, Exception? error = null) => Write("W", error is null ? message : $"{message}: {error.GetType().Name}: {error.Message}");

    public static void Error(string message, Exception? error = null) => Write("E", error is null ? message : $"{message}: {error}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} [{Environment.CurrentManagedThreadId}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            if (_writer is null) return;
            try
            {
                if (_writer.BaseStream.Length > MaxBytes)
                {
                    _writer.Dispose();
                    File.Move(CurrentPath, PreviousPath, overwrite: true);
                    _writer = new StreamWriter(new FileStream(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Падение: полный текст исключения в <c>last-crash.txt</c> и в журнал.</summary>
    public static void Crash(Exception error, string source)
    {
        Error($"Crash ({source})", error);
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
    /// «Сохранить журнал…» (tasks/0039): шапка, падение, прошлый запуск и этот — одним текстом (<see cref="LogReport"/>).
    /// Строки до нажатия уже на диске: журнал пишется без буфера.
    /// </summary>
    public static string Report() =>
        LogReport.Build(LogReport.Header(AppInfo.Version, WindowsVersion(), ArchitectureText(), DateTimeOffset.Now), AppPaths.Logs);

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
