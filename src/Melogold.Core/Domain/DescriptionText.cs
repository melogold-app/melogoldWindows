using System.Text.RegularExpressions;

namespace Melogold.Core.Domain;

/// <summary>Откуда взято описание: статья Википедии и лицензия, как их пишет YouTube Music.</summary>
/// <param name="ArticleUrl">адрес статьи на <c>*.wikipedia.org</c> (https)</param>
/// <param name="License">название лицензии: «Creative Commons Attribution CC-BY-SA 3.0»; null — не указано</param>
/// <param name="LicenseUrl">адрес текста лицензии; null — YouTube его обрезал, а по названию адрес не восстановить</param>
public sealed record DescriptionSource(string ArticleUrl, string? License, string? LicenseUrl);

/// <summary>
/// Описание альбома или исполнителя из YouTube Music. Разбора Википедии у клиента нет: YouTube сам берёт текст из
/// статьи и дописывает в конец строку «From Wikipedia (адрес) under Creative Commons Attribution CC-BY-SA 3.0 (адрес)»
/// (по-русски — «Из Википедии (…) по лицензии …»). Здесь эта строка отделяется от текста, чтобы показать её ссылкой.
/// </summary>
public static partial class DescriptionText
{
    [GeneratedRegex(@"https?://[^\s)]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?:under|по\s+лицензии|по|—|-)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseLeadRegex();

    /// <summary>Текст без строки об источнике и сам источник (null — строки нет или адрес не вики).</summary>
    public static (string Body, DescriptionSource? Source) Split(string? description)
    {
        var text = (description ?? "").Trim();
        if (text.Length == 0) return ("", null);
        var lines = text.Split('\n');
        var index = Array.FindLastIndex(lines, line => line.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase));
        // Строка об источнике — последняя: после неё текста нет
        if (index < 0 || lines.Skip(index + 1).Any(line => line.Trim().Length > 0)) return (text, null);
        var source = Parse(lines[index]);
        return source is null ? (text, null) : (string.Join('\n', lines.Take(index)).Trim(), source);
    }

    private static DescriptionSource? Parse(string line)
    {
        var article = UrlRegex().Matches(line).FirstOrDefault(m => IsWikipedia(m.Value));
        if (article is null) return null;
        // После «(адрес статьи)» — «under Creative Commons … 3.0 (адрес лицензии)»
        var rest = line[(article.Index + article.Length)..].TrimStart(')', ' ', '\t').Trim();
        var licenseUrl = UrlRegex().Match(rest) is { Success: true } found ? found.Value : null;
        var name = licenseUrl is null ? rest : rest[..rest.IndexOf(licenseUrl, StringComparison.Ordinal)];
        name = LicenseLeadRegex().Replace(name.Trim().TrimEnd('(', ' ', '.'), "").Trim();
        var license = name.Length > 0 ? name : null;
        // YouTube обрезает адрес («…/licenses/...»): такой не открыть — остаётся название
        if (licenseUrl is not null && (licenseUrl.EndsWith("...", StringComparison.Ordinal) || licenseUrl.EndsWith('…')))
            licenseUrl = license is null ? null : KnownLicense(license);
        return new DescriptionSource(article.Value, license, licenseUrl);
    }

    /// <summary>Википедия по https: только такой адрес открывается из окна (описание приходит из сети).</summary>
    public static bool IsWikipedia(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "wikipedia.org" || uri.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase));

    private static string? KnownLicense(string name) =>
        name.Contains("CC-BY-SA 3.0", StringComparison.OrdinalIgnoreCase) || name.Contains("CC BY-SA 3.0", StringComparison.OrdinalIgnoreCase) ? "https://creativecommons.org/licenses/by-sa/3.0/"
        : name.Contains("CC-BY-SA 4.0", StringComparison.OrdinalIgnoreCase) || name.Contains("CC BY-SA 4.0", StringComparison.OrdinalIgnoreCase) ? "https://creativecommons.org/licenses/by-sa/4.0/"
        : null;

    /// <summary>Лицензия открывается только с creativecommons.org по https.</summary>
    public static bool IsLicense(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "creativecommons.org" || uri.Host.EndsWith(".creativecommons.org", StringComparison.OrdinalIgnoreCase));
}
