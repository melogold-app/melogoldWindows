using System.Globalization;

namespace Melogold.Core.Domain;

/// <summary>Время в форме API (§1.5): отдаётся <c>YYYY-MM-DDTHH:mm:ss.sssZ</c>, внутри — epoch-мс UTC.</summary>
public static class IsoTime
{
    public static string Format(long epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Format(DateTimeOffset time) => Format(time.ToUnixTimeMilliseconds());

    public static long Parse(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds();

    public static long? TryParse(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.ToUnixTimeMilliseconds()
            : null;

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Строки по правилам API §1.4: длины в единицах UTF-16, обрезка не рвёт суррогатную пару.</summary>
public static class Utf16
{
    public static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var end = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }

    public static string? TruncateOrNull(string? value, int max) => value is null ? null : Truncate(value, max);
}

/// <summary>Длительность трека: «3:45», «1:02:10» ↔ мс (DESIGN §3.3).</summary>
public static class Durations
{
    public static long? ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        long total = 0;
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total * 1000;
    }

    public static string Format(long ms)
    {
        var totalSeconds = Math.Max(0, ms / 1000);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    public static string Format(TimeSpan time) => Format((long)time.TotalMilliseconds);
}
