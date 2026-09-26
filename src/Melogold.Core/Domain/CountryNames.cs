using System.Globalization;
using System.Runtime.InteropServices;

namespace Melogold.Core.Domain;

/// <summary>Название страны по коду из двух букв (задание 0010: «Недоступно в стране «Россия»»).</summary>
public static class CountryNames
{
    /// <summary>
    /// На языке интерфейса Melogold (<see cref="CultureInfo.CurrentUICulture"/>); неизвестный код — сам код.
    /// <see cref="RegionInfo.DisplayName"/> для этого не годится: он всегда на языке Windows, и в английском Melogold на
    /// русской Windows было бы «Unavailable in Россия». Поэтому — ICU, встроенный в Windows 10 1903+ (<c>icu.dll</c>).
    /// </summary>
    public static string Of(string code)
    {
        if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper)) return code;
        try
        {
            var buffer = new char[128];
            var status = 0;
            var length = GetDisplayCountry("_" + code, CultureInfo.CurrentUICulture.Name.Replace('-', '_'), buffer, buffer.Length, ref status);
            // Незнакомый код ICU возвращает как есть
            if (status <= 0 && length > 0) return new string(buffer, 0, length);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    [DllImport("icu.dll", EntryPoint = "uloc_getDisplayCountry", CharSet = CharSet.Unicode)]
    private static extern int GetDisplayCountry([MarshalAs(UnmanagedType.LPStr)] string locale, [MarshalAs(UnmanagedType.LPStr)] string displayLocale,
        [Out] char[] country, int capacity, ref int status);
}
