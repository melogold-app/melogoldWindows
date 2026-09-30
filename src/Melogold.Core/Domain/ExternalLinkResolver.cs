using System.Net;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>Куда ведёт ссылка другого сервиса в Melogold (tasks/0016).</summary>
public abstract record ExternalResolution
{
    /// <summary>Название и исполнитель известны — искать их на YouTube Music.</summary>
    public sealed record Search(string Query) : ExternalResolution;

    /// <summary>Плейлист другого сервиса: импорт — отдельная задача.</summary>
    public sealed record Playlist : ExternalResolution;

    /// <summary>Страница не открылась: нет сети.</summary>
    public sealed record Offline : ExternalResolution;

    /// <summary>По ссылке ничего годного.</summary>
    public sealed record NotFound : ExternalResolution;
}

/// <summary>
/// Ссылка Spotify, Apple Music, Яндекс Музыки, Deezer, Tidal или SoundCloud → что искать на YouTube Music (tasks/0016).
/// song.link без ключа закрыт (<c>401 PUBLIC_API_ACCESS_DEPRECATED</c>, уточнение 2026-09-30), поэтому читается начало
/// страницы самой ссылки (до 400 КБ, как браузер) и берутся название и исполнитель из её заголовка
/// (<see cref="PageTitles"/>). Короткая ссылка узнаётся по адресу после переходов. Запросы — по одному, ответ помнится
/// сутки.
/// </summary>
public sealed class ExternalLinkResolver(HttpClient http)
{
    private const int MaxBytes = 400_000;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    /// <summary>Сервисы отдают заголовки браузеру; мобильному — лёгкую страницу (как у Android).</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Linux; Android 16; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";

    private readonly Dictionary<string, (ExternalResolution Result, DateTime At)> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _one = new(1, 1);

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<ExternalResolution> ResolveAsync(MusicServiceLink link, CancellationToken ct = default)
    {
        if (link.Kind == MusicLinkKind.Playlist) return new ExternalResolution.Playlist();
        lock (_cache)
        {
            if (_cache.TryGetValue(link.Url, out var known) && DateTime.UtcNow - known.At < Keep) return known.Result;
        }
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await FetchAsync(link, ct).ConfigureAwait(false);
            if (result is not ExternalResolution.Offline)
            {
                lock (_cache) _cache[link.Url] = (result, DateTime.UtcNow);
            }
            return result;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<ExternalResolution> FetchAsync(MusicServiceLink link, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new ExternalResolution.NotFound();
            // Короткая ссылка (spotify.link, link.deezer.com): что это, видно по адресу после переходов
            var landed = response.RequestMessage?.RequestUri is { } uri ? MusicServiceLinkParser.Parse(uri.AbsoluteUri) : null;
            var known = landed is { Kind: not MusicLinkKind.Unknown } && landed.Service == link.Service ? landed : link;
            if (known.Kind == MusicLinkKind.Playlist) return new ExternalResolution.Playlist();
            var html = await ReadHeadAsync(response, ct).ConfigureAwait(false);
            return PageTitles.SearchText(known, html) is { } query ? new ExternalResolution.Search(query) : new ExternalResolution.NotFound();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ExternalResolution.Offline();
        }
    }

    /// <summary>Первые <see cref="MaxBytes"/> страницы — заголовок в её начале; кодировка из ответа, иначе UTF-8.</summary>
    private static async Task<string> ReadHeadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        Encoding encoding;
        try
        {
            encoding = response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(buffer, 0, read);
    }
}
