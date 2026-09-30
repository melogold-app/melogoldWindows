using System.Text.RegularExpressions;
using Melogold.Core.Music;

namespace Melogold.Core.Domain;

/// <summary>
/// Ссылки «Поделиться» (tasks/0016): трек, альбом, исполнитель и плейлист YouTube — их откроет любой — и текст, который
/// уходит вместе с ними.
/// </summary>
public static partial class ShareLinks
{
    private const string Music = "https://music.youtube.com";
    private const string YouTube = "https://www.youtube.com";

    /// <summary>Сколько первых треков открывает ссылка <c>watch_videos</c>: YouTube играет 50.</summary>
    public const int WatchVideosLimit = 50;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    /// <summary>Трек каталога — на YouTube Music, обычное видео — на YouTube.</summary>
    public static string Track(string videoId, bool isMusic) => $"{(isMusic ? Music : YouTube)}/watch?v={videoId}";

    public static string Track(Track track) => Track(track.VideoId, !track.IsVideo);

    public static string Album(string browseId) => $"{Music}/browse/{browseId}";

    /// <summary>Исполнитель каталога — на YouTube Music, канал YouTube (<paramref name="channel"/>) — на YouTube.</summary>
    public static string Artist(string browseId, bool channel) => $"{(channel ? YouTube : Music)}/channel/{browseId}";

    /// <summary>Плейлист YouTube; у <paramref name="playlistId"/> может остаться <c>VL</c> от browseId.</summary>
    public static string Playlist(string playlistId) =>
        $"{Music}/playlist?list={(playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId)}";

    /// <summary>
    /// Свой плейлист для того, у кого нет Melogold: первые <see cref="WatchVideosLimit"/> видео одним списком YouTube.
    /// Всё, что не id видео, пропускается; null — не осталось ничего.
    /// </summary>
    public static string? WatchVideos(IEnumerable<string> videoIds)
    {
        var ids = videoIds.Where(IsVideoId).Take(WatchVideosLimit).ToList();
        return ids.Count == 0 ? null : $"{YouTube}/watch_videos?video_ids={string.Join(",", ids)}";
    }

    public static bool IsVideoId(string id) => VideoIdRegex().IsMatch(id);

    /// <summary>«Название — исполнитель» и ссылка строкой ниже; без <paramref name="subtitle"/> — только название.</summary>
    public static string Message(string title, string? subtitle, string url)
    {
        var head = string.IsNullOrWhiteSpace(subtitle) ? title.Trim() : $"{title.Trim()} — {subtitle.Trim()}";
        return head.Length == 0 ? url : $"{head}\n{url}";
    }

    /// <summary>Ссылка на снимок плейлиста, которая открывается в приложении (API §7.2).</summary>
    public static string MelogoldShare(string serverUrl, string shareId) =>
        $"melogold://share?v=1&url={Uri.EscapeDataString(serverUrl)}&id={shareId}";
}

/// <summary>Снимок плейлиста на сервере Melogold: base URL сервера (API §7.1) и id снимка.</summary>
public sealed record ShareRef(string ServerUrl, string ShareId);

/// <summary>
/// Что указывает на снимок плейлиста (API §7.2), где бы оно ни стояло в тексте: ссылка приложения
/// <c>melogold://share?v=1&amp;url=&lt;base&gt;&amp;id=&lt;id&gt;</c> и ссылка страницы <c>https://&lt;сервер&gt;/s/&lt;id&gt;</c>.
/// Адрес сервера проверяется общими правилами (<see cref="ServerAddressPolicy"/>); в сеть здесь не ходят.
/// </summary>
public static partial class ShareLinkParser
{
    private const string Trailing = ".,;:!?)]}>»\"'…";

    [GeneratedRegex(@"(?i)(?:melogold|https?)://\S+")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("^[0-9A-Za-z]{10}$")]
    private static partial Regex ShareIdRegex();

    public static ShareRef? Parse(string? text)
    {
        var match = LinkRegex().Match(text ?? "");
        if (!match.Success) return null;
        var link = match.Value.TrimEnd(Trailing.ToCharArray());
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme.ToLowerInvariant() switch
        {
            "melogold" => FromDeepLink(uri),
            "http" or "https" => FromPage(uri),
            _ => null,
        };
    }

    private static ShareRef? FromDeepLink(Uri uri)
    {
        if (!uri.Host.Equals("share", StringComparison.OrdinalIgnoreCase)) return null;
        var query = Query(uri.Query);
        if (query.GetValueOrDefault("v") != "1") return null;
        if (ServerAddressPolicy.Normalize(query.GetValueOrDefault("url")) is not ServerAddress.Valid server) return null;
        return query.GetValueOrDefault("id") is { } id && ShareIdRegex().IsMatch(id) ? new ShareRef(server.Url, id) : null;
    }

    private static ShareRef? FromPage(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[^2] != "s" || !ShareIdRegex().IsMatch(segments[^1])) return null;
        var prefix = string.Join("/", segments[..^2]);
        var base_ = $"{uri.Scheme}://{uri.Authority}" + (prefix.Length == 0 ? "" : "/" + prefix);
        return ServerAddressPolicy.Normalize(base_) is ServerAddress.Valid server ? new ShareRef(server.Url, segments[^1]) : null;
    }

    private static Dictionary<string, string> Query(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Decode(eq < 0 ? pair : pair[..eq]);
            result.TryAdd(key, eq < 0 ? "" : Decode(pair[(eq + 1)..]));
        }
        return result;
    }

    private static string Decode(string value)
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
}
