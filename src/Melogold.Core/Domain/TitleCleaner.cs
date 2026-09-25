using System.Text;
using System.Text.RegularExpressions;

namespace Melogold.Core.Domain;

/// <summary>Как называется трек, когда шум названия YouTube убран.</summary>
public sealed record CleanTitle(string? Artist, string Title);

/// <summary>
/// Исполнитель и название трека YouTube для поиска текста и «Других версий» (векторы <c>spec/title-cleaner.vectors.json</c>).
/// По порядку: эмодзи, <c>【…】</c>, всё после " | ", скобки из одного шума («(Official Video)», «[HD]»), номер трека,
/// «Исполнитель - Название», «feat.», кавычки и пробелы. <paramref name="videoType"/>: <c>song</c>, <c>video</c>,
/// <c>ugc</c>, <c>live</c> или null; песни и клипы делят «Исполнитель - Название», только если слева канал.
/// </summary>
public static partial class TitleCleaner
{
    [GeneratedRegex("【[^】]*】")]
    private static partial Regex Lenticular();

    [GeneratedRegex(@"\s+\|.*$")]
    private static partial Regex PipeTail();

    [GeneratedRegex(@"\s*[(\[]([^()\[\]]*)[)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^(?:official( (music|lyric))? (video|audio|visualizer|clip)|official|((music|lyric) )?video|audio|lyrics?|visualizer|hd|hq|4k|8k|1080p|720p|mv|m/v|официальное видео|официальный клип|клип|премьера( клипа)?(,.*)?|текст( песни)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"^\d{1,3}\.\s+")]
    private static partial Regex TrackNumber();

    [GeneratedRegex(" [-–—] ")]
    private static partial Regex Separator();

    [GeneratedRegex(@"\s*[(\[](feat\.?|ft\.?|featuring)\s[^)\]]*[)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatGroup();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTail();

    [GeneratedRegex(@"\s*[-–—]\s*(topic|тема)$", RegexOptions.IgnoreCase)]
    private static partial Regex Topic();

    [GeneratedRegex("^[«\"“„](.*)[»\"”“]$")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static readonly string[] ChannelTails = ["vevo", "official"];

    public static CleanTitle Clean(string title, string? channel, string? videoType)
    {
        var upload = videoType is null or "ugc" or "live";

        var text = Lenticular().Replace(WithoutEmoji(title), " ");
        text = PipeTail().Replace(text, "");
        text = Brackets().Replace(text, match => Noise().IsMatch(match.Groups[1].Value.Trim()) ? "" : match.Value);
        text = Collapse(text);
        if (upload) text = TrackNumber().Replace(text, "");

        var channelArtist = channel is null ? null : Topic().Replace(channel.Trim(), "");
        if (string.IsNullOrWhiteSpace(channelArtist)) channelArtist = null;
        var artist = channelArtist;
        var separator = Separator().Match(text);
        if (separator.Success)
        {
            var left = text[..separator.Index].Trim();
            var isChannel = channelArtist is not null && Comparable(left) == Comparable(channelArtist);
            if (isChannel || upload)
            {
                artist = WithoutFeat(left);
                text = text[(separator.Index + separator.Length)..];
            }
        }

        text = Collapse(WithoutFeat(text));
        var quoted = Quoted().Match(text);
        if (quoted.Success) text = Collapse(quoted.Groups[1].Value);
        var cleanArtist = artist is null ? null : Collapse(artist);
        return new CleanTitle(string.IsNullOrEmpty(cleanArtist) ? null : cleanArtist, text);
    }

    private static string WithoutFeat(string value) => FeatTail().Replace(FeatGroup().Replace(value, ""), "").Trim();

    private static string Collapse(string value) => Spaces().Replace(value, " ").Trim();

    /// <summary>Имя для сравнения канала и левой части: без feat, строчными, только буквы и цифры.</summary>
    private static string Comparable(string value)
    {
        var name = new string(WithoutFeat(value).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        foreach (var tail in ChannelTails)
        {
            if (name.Length > tail.Length && name.EndsWith(tail, StringComparison.Ordinal)) name = name[..^tail.Length];
        }
        return name;
    }

    /// <summary>Эмодзи уходят: Extended_Pictographic, флаги, селектор варианта, соединители, keycap, тона кожи.</summary>
    private static string WithoutEmoji(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsEmojiPart(rune.Value)) builder.Append(' ');
            else builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static bool IsEmojiPart(int c) =>
        c == 0xFE0F || c == 0x200D || c == 0x20E3 ||
        c is >= 0x1F000 and <= 0x1FAFF || c is >= 0x1FC00 and <= 0x1FFFD || c is >= 0x2600 and <= 0x27BF ||
        c == 0x00A9 || c == 0x00AE || c == 0x203C || c == 0x2049 || c == 0x2122 || c == 0x2139 ||
        c is >= 0x2194 and <= 0x2199 || c is >= 0x21A9 and <= 0x21AA || c is >= 0x231A and <= 0x231B || c == 0x2328 ||
        c == 0x23CF || c is >= 0x23E9 and <= 0x23F3 || c is >= 0x23F8 and <= 0x23FA || c == 0x24C2 ||
        c is >= 0x25AA and <= 0x25AB || c == 0x25B6 || c == 0x25C0 || c is >= 0x25FB and <= 0x25FE ||
        c is >= 0x2934 and <= 0x2935 || c is >= 0x2B05 and <= 0x2B07 || c is >= 0x2B1B and <= 0x2B1C || c == 0x2B50 ||
        c == 0x2B55 || c == 0x3030 || c == 0x303D || c == 0x3297 || c == 0x3299;
}
