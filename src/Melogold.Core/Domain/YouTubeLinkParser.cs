using System.Globalization;
using System.Text.RegularExpressions;

namespace Melogold.Core.Domain;

/// <summary>Куда ведёт ссылка или текст из «Поделиться» (REWRITE §4.9 Android).</summary>
public abstract record LinkTarget
{
    public sealed record Video(string VideoId, string? PlaylistId = null, int? Index = null, long? StartMs = null) : LinkTarget;

    /// <summary>PL…, RDCLAK5uy_…, RD…, UU…, OLAK5uy_… (альбом: резолвер находит его по первому треку).</summary>
    public sealed record Playlist(string PlaylistId) : LinkTarget;

    public sealed record Album(string BrowseId) : LinkTarget;

    public sealed record Channel(string ChannelId) : LinkTarget;

    /// <summary><c>/@name</c>: нужен <c>resolve_url</c>.</summary>
    public sealed record Handle(string Name) : LinkTarget;

    /// <summary><c>/c/…</c>, <c>/user/…</c>: нужен <c>resolve_url</c>.</summary>
    public sealed record LegacyChannel(string Url) : LinkTarget;

    public sealed record Search(string Query) : LinkTarget;

    /// <summary>Apple Music, Яндекс Музыка или Spotify: импорт появится позже.</summary>
    public sealed record External(string Service, string Url) : LinkTarget;

    public sealed record Unsupported(string Reason) : LinkTarget;

    public const string ReasonEmpty = "empty";
    public const string ReasonInvalidVideoId = "invalid_video_id";
    public const string ReasonMissingParameter = "missing_parameter";
    public const string ReasonPrivatePlaylist = "private_playlist";
    public const string ReasonClip = "clip";
    public const string ReasonPost = "post";
    public const string ReasonUnknownPath = "unknown_path";
    public const string ReasonUnknownHost = "unknown_host";
    public const string ReasonUnsupportedExternal = "unsupported_external";
}

/// <summary>
/// Разбор ссылок YouTube и YouTube Music и текста из других приложений по векторам
/// <c>spec/youtube-links.vectors.json</c>. Без сети: <c>resolve_url</c> и альбом плейлиста <c>OLAK5uy_</c> — дело резолвера.
/// </summary>
public static partial class YouTubeLinkParser
{
    private const int MaxQueryLength = 200;
    private const int MaxUnwrapDepth = 3;
    private const string VndPrefix = "vnd.youtube:";
    private const string TrailingPunctuation = ".,;:!?)]}>»\"'…";

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://([^/?#]*)([^?#]*)(?:\?([^#]*))?(?:#.*)?$")]
    private static partial Regex UriRegex();

    private static readonly HashSet<string> YouTubeHosts =
    [
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "youtube-nocookie.com",
        "www.youtube-nocookie.com",
    ];

    private static readonly HashSet<string> GoogleHosts = ["google.com", "www.google.com"];

    /// <summary>Списки самого аккаунта: вне аккаунта смысла не имеют.</summary>
    private static readonly HashSet<string> PrivatePlaylists = ["LL", "WL", "LM"];

    private static readonly HashSet<string> VideoPathPrefixes = ["shorts", "live", "embed", "v", "e"];

    public static LinkTarget Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return new LinkTarget.Unsupported(LinkTarget.ReasonEmpty);

        if (text.StartsWith(VndPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[VndPrefix.Length..];
            var end = rest.IndexOfAny(['?', '&', '#']);
            return VideoTarget(end < 0 ? rest : rest[..end], new Dictionary<string, string>());
        }

        var match = UrlRegex().Match(text);
        if (!match.Success) return new LinkTarget.Search(SearchText(text));
        var url = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
        return ParseUrl(url, 0);
    }

    private static string SearchText(string text)
    {
        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        if (collapsed.Length <= MaxQueryLength) return collapsed;
        var end = char.IsHighSurrogate(collapsed[MaxQueryLength - 1]) ? MaxQueryLength - 1 : MaxQueryLength;
        return collapsed[..end];
    }

    private static LinkTarget ParseUrl(string url, int depth)
    {
        var match = UriRegex().Match(url);
        if (!match.Success) return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
        var authority = match.Groups[1].Value;
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        var colon = authority.LastIndexOf(':');
        var host = (colon >= 0 && !authority.EndsWith(']') ? authority[..colon] : authority).ToLowerInvariant();
        if (host.Length == 0) return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownHost);

        var rawPath = match.Groups[2].Value;
        var query = QueryParameters(match.Groups[3].Success ? match.Groups[3].Value : null);
        var rawSegments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = rawSegments.Select(DecodePath).ToArray();

        LinkTarget Unwrap(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
            if (depth >= MaxUnwrapDepth) return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
            return ParseUrl(target, depth + 1);
        }

        if (host == "consent.youtube.com") return Unwrap(query.GetValueOrDefault("continue"));
        if (GoogleHosts.Contains(host) && segments.FirstOrDefault() == "url")
            return Unwrap(query.GetValueOrDefault("q") ?? query.GetValueOrDefault("url"));

        var external = External(host, segments, url);
        if (external is not null) return external;
        if (!YouTubeHosts.Contains(host)) return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownHost);

        if (host == "youtu.be")
        {
            return segments.Length > 0
                ? VideoTarget(segments[0], query)
                : new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
        }

        if (segments.Length == 0) return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
        var first = segments[0];
        var second = segments.Length > 1 ? segments[1] : null;

        if (first == "attribution_link")
        {
            var u = query.GetValueOrDefault("u");
            return Unwrap(u is not null && u.StartsWith('/') ? "https://www.youtube.com" + u : u);
        }
        if (first == "watch")
        {
            var videoId = query.GetValueOrDefault("v");
            var list = query.GetValueOrDefault("list");
            if (videoId is not null) return VideoTarget(videoId, query);
            if (string.IsNullOrEmpty(list)) return new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
            if (PrivatePlaylists.Contains(list)) return new LinkTarget.Unsupported(LinkTarget.ReasonPrivatePlaylist);
            return new LinkTarget.Playlist(list);
        }
        if (VideoPathPrefixes.Contains(first))
        {
            return second is not null
                ? VideoTarget(second, query)
                : new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
        }
        if (first == "playlist")
        {
            var list = query.GetValueOrDefault("list");
            if (string.IsNullOrEmpty(list)) return new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
            if (PrivatePlaylists.Contains(list)) return new LinkTarget.Unsupported(LinkTarget.ReasonPrivatePlaylist);
            return new LinkTarget.Playlist(list);
        }
        if (first == "browse")
        {
            if (second is null) return new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
            if (second.StartsWith("VL", StringComparison.Ordinal)) return new LinkTarget.Playlist(second[2..]);
            if (second.StartsWith("MPREb_", StringComparison.Ordinal)) return new LinkTarget.Album(second);
            if (second.StartsWith("UC", StringComparison.Ordinal)) return new LinkTarget.Channel(second);
            return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
        }
        if (first == "channel")
        {
            if (second is null) return new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
            return second.StartsWith("UC", StringComparison.Ordinal)
                ? new LinkTarget.Channel(second)
                : new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
        }
        if (first.StartsWith('@') && first.Length > 1) return new LinkTarget.Handle(first[1..]);
        if (first is "c" or "user")
        {
            return rawSegments.Length > 1
                ? new LinkTarget.LegacyChannel($"https://www.youtube.com/{first}/{rawSegments[1]}")
                : new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
        }
        if (first == "search")
        {
            var q = query.GetValueOrDefault("q");
            return string.IsNullOrWhiteSpace(q) ? new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter) : new LinkTarget.Search(q);
        }
        if (first == "results")
        {
            var q = query.GetValueOrDefault("search_query");
            return string.IsNullOrWhiteSpace(q) ? new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter) : new LinkTarget.Search(q);
        }
        if (first == "hashtag")
        {
            return second is not null ? new LinkTarget.Search("#" + second) : new LinkTarget.Unsupported(LinkTarget.ReasonMissingParameter);
        }
        if (first == "clip") return new LinkTarget.Unsupported(LinkTarget.ReasonClip);
        if (first == "post") return new LinkTarget.Unsupported(LinkTarget.ReasonPost);
        return new LinkTarget.Unsupported(LinkTarget.ReasonUnknownPath);
    }

    private static LinkTarget? External(string host, string[] segments, string url)
    {
        string? service;
        switch (host)
        {
            case "music.apple.com":
                service = segments.Contains("playlist") ? "apple" : null;
                break;
            case "music.yandex.ru":
            case "music.yandex.com":
                service = segments.Contains("playlists") || segments.FirstOrDefault() == "album" ? "yandex" : null;
                break;
            case "open.spotify.com":
                service = segments.FirstOrDefault() == "playlist" ? "spotify" : null;
                break;
            default:
                return null;
        }
        return service is not null
            ? new LinkTarget.External(service, url)
            : new LinkTarget.Unsupported(LinkTarget.ReasonUnsupportedExternal);
    }

    private static LinkTarget VideoTarget(string videoId, IReadOnlyDictionary<string, string> query)
    {
        if (!VideoIdRegex().IsMatch(videoId)) return new LinkTarget.Unsupported(LinkTarget.ReasonInvalidVideoId);
        var list = query.GetValueOrDefault("list");
        int? index = int.TryParse(query.GetValueOrDefault("index"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : null;
        var time = query.GetValueOrDefault("t") ?? query.GetValueOrDefault("start");
        return new LinkTarget.Video(
            videoId,
            !string.IsNullOrEmpty(list) && !PrivatePlaylists.Contains(list) ? list : null,
            index,
            time is null ? null : TimeMs(time));
    }

    /// <summary>"90", "90s", "1m30s", "1h2m3s" → миллисекунды; иначе null.</summary>
    private static long? TimeMs(string value)
    {
        if (value.Length == 0) return null;
        var match = TimeRegex().Match(value);
        if (!match.Success) return null;
        var (h, m, s) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        if (h.Length == 0 && m.Length == 0 && s.Length == 0) return null;
        static long Parse(string text) => text.Length == 0 ? 0 : long.Parse(text, CultureInfo.InvariantCulture);
        return (Parse(h) * 3600 + Parse(m) * 60 + Parse(s)) * 1000;
    }

    private static Dictionary<string, string> QueryParameters(string? rawQuery)
    {
        var parameters = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(rawQuery)) return parameters;
        foreach (var pair in rawQuery.Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var key = Decode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Decode(pair[(eq + 1)..]);
            parameters.TryAdd(key, value);
        }
        return parameters;
    }

    /// <summary>Путь: только percent-escapes, <c>+</c> остаётся (как <c>URI.getPath</c> в Java).</summary>
    private static string DecodePath(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>Как <c>URLDecoder</c> в Java: <c>+</c> — пробел, неверная последовательность — строка как есть.</summary>
    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
