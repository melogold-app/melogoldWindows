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
        var form = Melogold.Core.Domain.Plurals.Form(count, IsRussian);
        return string.Format(CultureInfo.CurrentCulture, Get($"{key}_{form}"), count.ToString("N0", CultureInfo.CurrentCulture));
    }
}
