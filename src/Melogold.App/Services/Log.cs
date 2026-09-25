using System.Globalization;
using System.Text;

namespace Melogold.App.Services;

/// <summary>
/// Журнал (docs/PROMPT.md §3): <c>logs\current.log</c> до 2 МБ, при старте прежний переименовывается в
/// <c>previous.log</c>; последнее падение — отдельным файлом <c>last-crash.txt</c>. Пароли и токены сюда не пишутся.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;

    public static string CurrentPath => Path.Combine(AppPaths.Logs, "current.log");

    public static string PreviousPath => Path.Combine(AppPaths.Logs, "previous.log");

    public static string CrashPath => Path.Combine(AppPaths.Logs, "last-crash.txt");

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
}
