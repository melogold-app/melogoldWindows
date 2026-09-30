using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Melogold.Core.Domain;

/// <summary>Другие музыкальные сервисы, чьи ссылки открываются в Melogold (tasks/0016).</summary>
public enum MusicService
{
    Spotify,
    AppleMusic,
    YandexMusic,
    Deezer,
    Tidal,
    SoundCloud,
}

/// <summary>На что указывает ссылка; <see cref="Unknown"/> — короткая ссылка, куда она ведёт, видно только после перехода.</summary>
public enum MusicLinkKind
{
    Track,
    Album,
    Artist,
    Playlist,
    Unknown,
}

/// <summary>Ссылка на трек, альбом, исполнителя или плейлист другого сервиса.</summary>
public sealed record MusicServiceLink(MusicService Service, MusicLinkKind Kind, string Url);

/// <summary>
/// Ссылки Spotify, Apple Music, Яндекс Музыки, Deezer, Tidal и SoundCloud, где бы они ни стояли в тексте, и что каждая
/// из них; как у Android (<c>MusicServiceLinkParser.kt</c>). В сеть не ходит.
/// </summary>
public static partial class MusicServiceLinkParser
{
    private const string Trailing = ".,;:!?)]}>»\"'…";

    private static readonly HashSet<string> Spotify = ["open.spotify.com", "play.spotify.com", "spotify.link", "spotify.app.link"];
    private static readonly HashSet<string> Apple = ["music.apple.com", "itunes.apple.com", "geo.music.apple.com"];
    private static readonly HashSet<string> Deezer = ["deezer.com", "link.deezer.com", "deezer.page.link", "dzr.page.link"];
    private static readonly HashSet<string> Tidal = ["tidal.com", "listen.tidal.com", "link.tidal.com"];
    private static readonly HashSet<string> SoundCloud = ["soundcloud.com", "m.soundcloud.com", "on.soundcloud.com"];

    [GeneratedRegex(@"(?i)https?://\S+")]
    private static partial Regex UrlRegex();

    public static MusicServiceLink? Parse(string? input)
    {
        var match = UrlRegex().Match(input ?? "");
        if (!match.Success) return null;
        var url = match.Value.TrimEnd(Trailing.ToCharArray());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        MusicServiceLink? Link(MusicService service, MusicLinkKind? kind) => kind is { } k ? new MusicServiceLink(service, k, url) : null;
        if (Spotify.Contains(host)) return Link(MusicService.Spotify, SpotifyKind(host, segments));
        if (Apple.Contains(host)) return Link(MusicService.AppleMusic, AppleKind(segments, uri.Query));
        if (host.StartsWith("music.yandex.", StringComparison.Ordinal)) return Link(MusicService.YandexMusic, YandexKind(segments));
        if (Deezer.Contains(host)) return Link(MusicService.Deezer, DeezerKind(host, segments));
        if (Tidal.Contains(host)) return Link(MusicService.Tidal, TidalKind(host, segments));
        if (SoundCloud.Contains(host)) return Link(MusicService.SoundCloud, SoundCloudKind(host, segments));
        return null;
    }

    /// <summary><c>open.spotify.com/intl-de/track/&lt;id&gt;</c> — с языком впереди; подкаст и прочее — не музыка.</summary>
    private static MusicLinkKind? SpotifyKind(string host, string[] segments)
    {
        if (host is not ("open.spotify.com" or "play.spotify.com")) return MusicLinkKind.Unknown;
        var path = segments.FirstOrDefault()?.StartsWith("intl-", StringComparison.Ordinal) == true ? segments[1..] : segments;
        return path.FirstOrDefault() switch
        {
            "track" => MusicLinkKind.Track,
            "album" => MusicLinkKind.Album,
            "artist" => MusicLinkKind.Artist,
            "playlist" => MusicLinkKind.Playlist,
            _ => null,
        };
    }

    /// <summary><c>music.apple.com/&lt;страна&gt;/album/&lt;имя&gt;/&lt;id&gt;?i=&lt;трек&gt;</c> — трек альбома.</summary>
    private static MusicLinkKind AppleKind(string[] segments, string query) =>
        segments.FirstOrDefault(s => s is "song" or "album" or "playlist" or "artist" or "music-video") switch
        {
            "song" => MusicLinkKind.Track,
            "album" => HasParameter(query, "i") ? MusicLinkKind.Track : MusicLinkKind.Album,
            "playlist" => MusicLinkKind.Playlist,
            "artist" => MusicLinkKind.Artist,
            _ => MusicLinkKind.Unknown,
        };

    private static MusicLinkKind YandexKind(string[] segments) =>
        segments.Contains("playlists") ? MusicLinkKind.Playlist
        : segments.ElementAtOrDefault(0) == "album" && segments.ElementAtOrDefault(2) == "track" ? MusicLinkKind.Track
        : segments.ElementAtOrDefault(0) == "track" ? MusicLinkKind.Track
        : segments.ElementAtOrDefault(0) == "album" ? MusicLinkKind.Album
        : segments.ElementAtOrDefault(0) == "artist" ? MusicLinkKind.Artist
        : MusicLinkKind.Unknown;

    /// <summary><c>deezer.com/&lt;язык&gt;/track/&lt;id&gt;</c>: язык может быть, а может не быть.</summary>
    private static MusicLinkKind DeezerKind(string host, string[] segments) => host != "deezer.com"
        ? MusicLinkKind.Unknown
        : Kind(segments.FirstOrDefault(s => s is "track" or "album" or "artist" or "playlist"));

    private static MusicLinkKind TidalKind(string host, string[] segments) => host == "link.tidal.com"
        ? MusicLinkKind.Unknown
        : segments.FirstOrDefault(s => s is "track" or "album" or "artist" or "playlist" or "mix") switch
        {
            "mix" => MusicLinkKind.Playlist,
            var kind => Kind(kind),
        };

    /// <summary><c>soundcloud.com/&lt;автор&gt;/&lt;трек&gt;</c>, <c>/&lt;автор&gt;/sets/&lt;подборка&gt;</c>, <c>/&lt;автор&gt;</c>.</summary>
    private static MusicLinkKind SoundCloudKind(string host, string[] segments) =>
        host == "on.soundcloud.com" ? MusicLinkKind.Unknown
        : segments.Length >= 3 && segments[1] == "sets" ? MusicLinkKind.Playlist
        : segments.Length == 2 && segments[1] != "sets" ? MusicLinkKind.Track
        : segments.Length == 1 ? MusicLinkKind.Artist
        : MusicLinkKind.Unknown;

    private static MusicLinkKind Kind(string? segment) => segment switch
    {
        "track" => MusicLinkKind.Track,
        "album" => MusicLinkKind.Album,
        "artist" => MusicLinkKind.Artist,
        "playlist" => MusicLinkKind.Playlist,
        _ => MusicLinkKind.Unknown,
    };

    private static bool HasParameter(string query, string name) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Any(p => p == name || p.StartsWith(name + "=", StringComparison.Ordinal));
}

/// <summary>
/// Что искать по ссылке другого сервиса, когда song.link недоступен (tasks/0016, уточнение 2026-09-30): название и
/// исполнитель из начала её страницы — <c>&lt;title&gt;</c>, <c>og:title</c>, <c>og:description</c>. У каждого сервиса свои
/// правила, как у Android (<c>PageTitles.kt</c>); страница, где ничего годного нет (SoundCloud), — null.
/// </summary>
public static partial class PageTitles
{
    private const int MaxQuery = 120;

    // «Never Gonna Give You Up - song and lyrics by Rick Astley | Spotify»
    [GeneratedRegex(@"^(.+?) - (?:song and lyrics|song|album|single|EP)(?: and lyrics)? by (.+?) \| Spotify$", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyTitle();

    // «Never Gonna Give You Up - Song with Lyrics by Rick Astley - Apple Music»
    [GeneratedRegex(@"^(.+?) - (?:Song|Album|Single|EP)(?: with Lyrics)? by (.+?) - Apple Music$", RegexOptions.IgnoreCase)]
    private static partial Regex AppleTitle();

    [GeneratedRegex(@"^(.+) by (.+?) on Apple Music$", RegexOptions.IgnoreCase)]
    private static partial Regex AppleOg();

    [GeneratedRegex(@"\s+слушать онлайн.*$", RegexOptions.IgnoreCase)]
    private static partial Regex YandexTail();

    [GeneratedRegex(@"^(.+) by (.+?) on TIDAL$", RegexOptions.IgnoreCase)]
    private static partial Regex TidalTitle();

    // Старый заголовок трека: «Stream Never Gonna Give You Up by Rick Astley | Listen online for free on SoundCloud»
    [GeneratedRegex(@"^Stream (.+?) by (.+?) \|", RegexOptions.IgnoreCase)]
    private static partial Regex SoundCloudTitle();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"content\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContentAttribute();

    [GeneratedRegex(@"&(#x[0-9a-fA-F]+|#\d+|[a-zA-Z]+);")]
    private static partial Regex Entity();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["quot"] = "\"", ["apos"] = "'", ["lt"] = "<", ["gt"] = ">", ["nbsp"] = " ",
    };

    /// <summary>Невидимые метки направления и нулевой ширины, которые сервисы ставят в заголовки.</summary>
    private const string Invisible = "‎‏​﻿‪‬";

    public static string? SearchText(MusicServiceLink link, string html)
    {
        var title = TitleTag().Match(html) is { Success: true } match ? Clean(match.Groups[1].Value) : null;
        var og = Meta(html, "og:title");
        var description = Meta(html, "og:description");
        var query = link.Service switch
        {
            MusicService.Spotify => FromSpotify(link.Kind, title, og, description),
            MusicService.AppleMusic => FromApple(link.Kind, title, og),
            MusicService.YandexMusic => FromYandex(link.Kind, title, og, description),
            MusicService.Tidal => FromTidal(link.Kind, title, og),
            MusicService.Deezer => FromDeezer(title, og),
            _ => FromSoundCloud(title),
        };
        return query is null ? null : Tidy(query) is { Length: > 0 } tidy ? tidy : null;
    }

    private static string? FromSpotify(MusicLinkKind kind, string? title, string? og, string? description)
    {
        if (title is not null && SpotifyTitle().Match(title) is { Success: true } m) return $"{m.Groups[1].Value} {m.Groups[2].Value}";
        var name = og ?? RemoveSuffix(title, " | Spotify");
        if (kind == MusicLinkKind.Artist) return name;
        // Страница трека: og:title — название, описание начинается с исполнителя («Rick Astley · Album · Song · 1987»)
        var artist = description is not null && description.Contains(" · ", StringComparison.Ordinal) ? description[..description.IndexOf(" · ", StringComparison.Ordinal)] : null;
        return string.Join(" ", new[] { name, artist }.Where(s => s is not null));
    }

    private static string? FromApple(MusicLinkKind kind, string? title, string? og)
    {
        if (title is not null && AppleTitle().Match(title) is { Success: true } m) return $"{m.Groups[1].Value} {m.Groups[2].Value}";
        if (og is not null && AppleOg().Match(og) is { Success: true } o) return $"{o.Groups[1].Value} {o.Groups[2].Value}";
        return kind == MusicLinkKind.Artist ? RemoveSuffix(og, " on Apple Music") ?? RemoveSuffix(title, " - Apple Music") : null;
    }

    // «Never Gonna Give You Up Rick Astley слушать онлайн на Яндекс Музыке», название и «Rick Astley • Трек • 2019»
    private static string? FromYandex(MusicLinkKind kind, string? title, string? og, string? description)
    {
        var artist = description is not null && description.Contains(" • ", StringComparison.Ordinal) ? description[..description.IndexOf(" • ", StringComparison.Ordinal)] : null;
        var fromTitle = title is null ? null : YandexTail().Replace(title, "");
        if (kind == MusicLinkKind.Artist) return og ?? fromTitle;
        return og is not null ? string.Join(" ", new[] { og, artist }.Where(s => s is not null)) : fromTitle;
    }

    // «Never Gonna Give You Up by Rick Astley on TIDAL», og:title «Rick Astley - Never Gonna Give You Up»
    private static string? FromTidal(MusicLinkKind kind, string? title, string? og)
    {
        if (title is not null && TidalTitle().Match(title) is { Success: true } m) return $"{m.Groups[1].Value} {m.Groups[2].Value}";
        if (kind == MusicLinkKind.Artist) return og ?? RemoveSuffix(title, " on TIDAL");
        return og?.Replace(" - ", " ", StringComparison.Ordinal);
    }

    // «Daft Punk - Harder, Better, Faster, Stronger | Deezer»
    private static string? FromDeezer(string? title, string? og)
    {
        var head = title is null ? og : RemoveSuffix(title, "| Deezer")!.Trim().TrimEnd('|').Trim();
        return head?.Replace(" - ", " ", StringComparison.Ordinal);
    }

    private static string? FromSoundCloud(string? title) =>
        title is not null && SoundCloudTitle().Match(title) is { Success: true } m ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : null;

    private static string? RemoveSuffix(string? text, string suffix) =>
        text is not null && text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    /// <summary><c>content</c> у <c>&lt;meta property="name" …&gt;</c> (или <c>name=</c>), атрибуты в любом порядке.</summary>
    private static string? Meta(string html, string name)
    {
        var named = new Regex($@"(?:property|name)\s*=\s*([""']){Regex.Escape(name)}\1", RegexOptions.IgnoreCase);
        foreach (Match tag in MetaTag().Matches(html))
        {
            if (!named.IsMatch(tag.Value)) continue;
            if (ContentAttribute().Match(tag.Value) is { Success: true } content) return Clean(content.Groups[2].Value);
        }
        return null;
    }

    /// <summary>Сущности HTML — в знаки, невидимые метки — прочь, неразрывные пробелы — в обычные.</summary>
    private static string Clean(string raw)
    {
        var decoded = Entity().Replace(raw, match =>
        {
            var body = match.Groups[1].Value;
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(body[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex <= 0x10FFFF ? char.ConvertFromUtf32(hex) : match.Value;
            if (body.StartsWith('#'))
                return int.TryParse(body[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code <= 0x10FFFF ? char.ConvertFromUtf32(code) : match.Value;
            return Named.GetValueOrDefault(body.ToLowerInvariant()) ?? match.Value;
        });
        var text = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (Invisible.Contains(c, StringComparison.Ordinal)) continue;
            text.Append(c is ' ' or ' ' or ' ' ? ' ' : c);
        }
        return text.ToString().Trim();
    }

    private static string Tidy(string text)
    {
        var collapsed = Spaces().Replace(text, " ").Trim();
        if (collapsed.Length <= MaxQuery) return collapsed;
        var end = char.IsHighSurrogate(collapsed[MaxQuery - 1]) ? MaxQuery - 1 : MaxQuery;
        return collapsed[..end].Trim();
    }
}
