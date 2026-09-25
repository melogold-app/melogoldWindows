namespace Melogold.App.Services;

/// <summary>Первый запуск после установки (Velopack <c>OnFirstRun</c>): приложение показывает приветствие один раз.</summary>
public static class FirstRun
{
    private static string MarkerPath => Path.Combine(AppPaths.DataDirectory, "first-run");

    public static void Mark()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Был ли это первый запуск; метка снимается.</summary>
    public static bool Consume()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            File.Delete(MarkerPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
