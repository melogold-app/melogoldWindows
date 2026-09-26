using System.Text.RegularExpressions;

namespace Melogold.Core.Music;

/// <summary>
/// Обложка нужного размера (REWRITE §4.8.2 Android). В базе лежит исходный URL, размер подбирается по месту:
/// у <c>lh3</c>/<c>yt3.googleusercontent</c> хвост после <c>=</c> заменяется на <c>=w{px}-h{px}-l90-rj</c>;
/// у <c>i.ytimg.com/vi/&lt;id&gt;</c> до 320 px — <c>mqdefault.jpg</c>, иначе <c>hq720.jpg</c>.
/// </summary>
public static partial class Thumbnails
{
    [GeneratedRegex(@"^https?://i\.ytimg\.com/vi(_webp)?/([A-Za-z0-9_-]{11})/")]
    private static partial Regex YtImg();

    public static string? Sized(string? url, int px)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        if (url.Contains("googleusercontent.com", StringComparison.Ordinal) || url.Contains("ggpht.com", StringComparison.Ordinal))
        {
            var eq = url.LastIndexOf('=');
            var baseUrl = eq > url.IndexOf("://", StringComparison.Ordinal) + 3 ? url[..eq] : url;
            return $"{baseUrl}=w{px}-h{px}-l90-rj";
        }
        var match = YtImg().Match(url);
        if (match.Success)
        {
            var id = match.Groups[2].Value;
            return px <= 320 ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : $"https://i.ytimg.com/vi/{id}/hq720.jpg";
        }
        return url;
    }

    /// <summary>Обложка видео по его id, когда своей нет.</summary>
    public static string ForVideo(string videoId, int px = 544) =>
        px <= 320 ? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg" : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

    /// <summary>
    /// Запасной кадр, когда большого нет: у старых видео (2005–2012) <c>hq720.jpg</c>, <c>sddefault.jpg</c> и
    /// <c>maxresdefault.jpg</c> отвечают 404, есть только <c>hqdefault.jpg</c> 480×360 — без запаса «Сейчас играет»
    /// оставалось пустым. null — запасного нет.
    /// </summary>
    public static string? Fallback(string url)
    {
        var match = YtImg().Match(url);
        if (!match.Success) return null;
        var file = url[match.Length..];
        var query = file.IndexOf('?');
        if (query >= 0) file = file[..query];
        return file is "hq720.jpg" or "sddefault.jpg" or "maxresdefault.jpg" or "hq720.webp" or "maxresdefault.webp"
            ? $"https://i.ytimg.com/vi/{match.Groups[2].Value}/hqdefault.jpg"
            : null;
    }

    /// <summary>Обложка видео 16:9 (её нужно обрезать до квадрата при показе).</summary>
    public static bool IsWide(string? url) => url is not null && YtImg().IsMatch(url);
}
