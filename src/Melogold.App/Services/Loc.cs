using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Melogold.App.Services;

/// <summary>
/// Строки интерфейса из <c>Strings/*/Resources.resw</c> (источник — <c>tools/strings.tsv</c>). Множественное число —
/// ключи с суффиксами <c>_one</c>, <c>_few</c>, <c>_many</c> (русский) и <c>_one</c>, <c>_other</c> (английский),
/// правила — docs/GLOSSARY.md §1.4 Android.
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader? Loader = Create();

    private static ResourceLoader? Create()
    {
        try
        {
            return new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath());
        }
        catch (Exception e)
        {
            Log.Warn("Resources unavailable", e);
            return null;
        }
    }

    public static bool IsRussian { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    public static string Get(string key)
    {
        try
        {
            var value = Loader?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>«21 трек», «3 трека», «5 треков»: ключ <paramref name="key"/> с суффиксом формы, <c>{0}</c> — число.</summary>
    public static string Plural(string key, long count)
    {
        string form;
        if (IsRussian)
        {
            var n10 = count % 10;
            var n100 = count % 100;
            form = n10 == 1 && n100 != 11 ? "one" : n10 is >= 2 and <= 4 && n100 is < 12 or > 14 ? "few" : "many";
        }
        else form = count == 1 ? "one" : "other";
        return string.Format(CultureInfo.CurrentCulture, Get($"{key}_{form}"), count.ToString("N0", CultureInfo.CurrentCulture));
    }
}
