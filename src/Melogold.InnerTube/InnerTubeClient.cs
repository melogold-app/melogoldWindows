using Melogold.Core.Domain;
using Melogold.Core.Music;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Melogold.InnerTube;

/// <summary>Клиент InnerTube: имя, версия, заголовки. Свой User-Agent у каждого (REWRITE §4.14.1 Android).</summary>
public sealed record ClientProfile(
    string Name,
    int Id,
    string Version,
    string Host,
    string UserAgent,
    string? Referer = null,
    string? Platform = null,
    string? DeviceMake = null,
    string? DeviceModel = null,
    string? OsName = null,
    string? OsVersion = null,
    int? AndroidSdkVersion = null,
    string? MediaUserAgent = null)
{
    /// <summary>YouTube Music в браузере: поиск, страницы, очередь.</summary>
    public static readonly ClientProfile WebRemix = new(
        "WEB_REMIX", 67, "1.20260922.01.00", "music.youtube.com",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        Referer: "https://music.youtube.com/", Platform: "DESKTOP");

    /// <summary>Обычный YouTube: видео, каналы, трансляции вне каталога YTM.</summary>
    public static readonly ClientProfile Web = new(
        "WEB", 1, "2.20260924.00.00", "www.youtube.com",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        Referer: "https://www.youtube.com/", Platform: "DESKTOP");

    /// <summary>Поток: ссылки без подписи и без JS-проверок.</summary>
    public static readonly ClientProfile Ios = new(
        "IOS", 5, "20.10.4", "www.youtube.com",
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
        DeviceMake: "Apple", DeviceModel: "iPhone16,2", OsName: "iPhone", OsVersion: "18.3.2.22D82");

    /// <summary>Запасной клиент потока; запросы к googlevideo — с тем же User-Agent.</summary>
    public static readonly ClientProfile AndroidVr = new(
        "ANDROID_VR", 28, "1.65.10", "www.youtube.com",
        "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
        DeviceMake: "Oculus", DeviceModel: "Quest 3", OsName: "Android", OsVersion: "12L", AndroidSdkVersion: 32,
        MediaUserAgent: "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip");

    /// <summary>Только синхронный текст YouTube Music (<c>timedLyricsModel</c>).</summary>
    public static readonly ClientProfile AndroidMusic = new(
        "ANDROID_MUSIC", 21, "7.27.52", "music.youtube.com",
        "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 11) gzip",
        Platform: "MOBILE", OsName: "Android", OsVersion: "11", AndroidSdkVersion: 30);
}

/// <summary>Ошибка запроса к YouTube: <see cref="Kind"/> даёт класс состояния экрана (REWRITE §3.0 Android).</summary>
public sealed class YouTubeException(YouTubeErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public YouTubeErrorKind Kind { get; } = kind;
}

public enum YouTubeErrorKind
{
    /// <summary>Нет соединения с YouTube.</summary>
    Offline,

    /// <summary>Проверка на бота или блокировка.</summary>
    Blocked,

    /// <summary>YouTube изменил страницу.</summary>
    Parser,

    Unknown,
}

/// <summary>
/// Запросы к <c>/youtubei/v1</c>. Язык и регион берутся из системы (<c>ru-RU</c> → <c>hl=ru</c>, <c>gl=RU</c>),
/// <c>visitorData</c> запоминается из первого ответа.
/// </summary>
public sealed class InnerTubeClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _visitorData;

    public InnerTubeClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        (Language, Region) = LocaleFrom(CultureInfo.CurrentUICulture);
    }

    public string Language { get; set; }

    public string Region { get; set; }

    /// <summary><c>ru-RU</c> → (<c>ru</c>, <c>RU</c>); неизвестный язык — <c>en</c>, регион — <c>US</c>.</summary>
    public static (string Language, string Region) LocaleFrom(CultureInfo culture)
    {
        var tag = culture.Name.Replace("-Hant", "", StringComparison.Ordinal);
        var language = SupportedLanguages.Contains(tag) ? tag
            : SupportedLanguages.Contains(culture.TwoLetterISOLanguageName) ? culture.TwoLetterISOLanguageName : "en";
        var region = "US";
        try
        {
            if (!culture.IsNeutralCulture && culture.Name.Length > 0)
            {
                var info = new RegionInfo(culture.Name);
                if (info.TwoLetterISORegionName.Length == 2) region = info.TwoLetterISORegionName;
            }
        }
        catch (ArgumentException)
        {
        }
        return (language, region);
    }

    private static readonly HashSet<string> SupportedLanguages =
    [
        "af", "az", "id", "ms", "ca", "cs", "da", "de", "et", "en-GB", "en", "es", "es-419", "eu", "fil", "fr", "fr-CA", "gl", "hr",
        "zu", "is", "it", "sw", "lt", "hu", "nl", "no", "uz", "pl", "pt-PT", "pt", "ro", "sq", "sk", "sl", "fi", "sv", "vi", "tr",
        "bg", "ky", "kk", "mk", "mn", "ru", "sr", "uk", "el", "hy", "iw", "ur", "ar", "fa", "ne", "mr", "hi", "bn", "pa", "gu",
        "ta", "te", "kn", "ml", "si", "th", "lo", "my", "ka", "am", "km", "zh-CN", "zh-TW", "zh-HK", "ja", "ko",
    ];

    public JsonObject Context(ClientProfile client)
    {
        var c = new JsonObject
        {
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["hl"] = Language,
            ["gl"] = Region,
            ["timeZone"] = "UTC",
            ["utcOffsetMinutes"] = 0,
        };
        if (client.Platform is not null) c["platform"] = client.Platform;
        if (client.DeviceMake is not null) c["deviceMake"] = client.DeviceMake;
        if (client.DeviceModel is not null) c["deviceModel"] = client.DeviceModel;
        if (client.OsName is not null) c["osName"] = client.OsName;
        if (client.OsVersion is not null) c["osVersion"] = client.OsVersion;
        if (client.AndroidSdkVersion is { } sdk) c["androidSdkVersion"] = sdk;
        if (_visitorData is not null) c["visitorData"] = _visitorData;
        return new JsonObject { ["client"] = c, ["user"] = new JsonObject { ["lockedSafetyMode"] = false } };
    }

    public async Task<JsonNode> PostAsync(ClientProfile client, string endpoint, JsonObject body, CancellationToken cancellationToken = default)
    {
        body["context"] = Context(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{client.Host}/youtubei/v1/{endpoint}?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", client.Id.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.Version);
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(Language));
        if (client.Referer is not null)
        {
            request.Headers.TryAddWithoutValidation("Referer", client.Referer);
            request.Headers.TryAddWithoutValidation("Origin", client.Referer.TrimEnd('/'));
        }
        if (_visitorData is not null) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", _visitorData);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new YouTubeException(YouTubeErrorKind.Offline, e.Message, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YouTubeException(YouTubeErrorKind.Offline, "Timeout", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden ? YouTubeErrorKind.Blocked
                    : (int)response.StatusCode >= 500 ? YouTubeErrorKind.Offline : YouTubeErrorKind.Unknown;
                throw new YouTubeException(kind, $"HTTP {(int)response.StatusCode} from {endpoint}");
            }
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new YouTubeException(YouTubeErrorKind.Parser, "Not JSON", e);
            }
            if (node is null) throw new YouTubeException(YouTubeErrorKind.Parser, "Empty response");
            if (_visitorData is null && node.Str("responseContext", "visitorData") is { Length: > 0 } visitor) _visitorData = visitor;
            return node;
        }
    }

    public void Dispose() => _http.Dispose();
}
