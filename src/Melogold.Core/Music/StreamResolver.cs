using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Melogold.Core.Music;

/// <summary>Адрес аудиопотока трека и то, как по нему ходить.</summary>
public sealed record StreamInfo
{
    public required string VideoId { get; init; }
    public required string Url { get; init; }
    public int Itag { get; init; }
    public string MimeType { get; init; } = "audio/mp4";
    public long? ContentLength { get; init; }
    public int? Bitrate { get; init; }

    /// <summary>Параметр <c>expire</c> адреса минус 5 минут (REWRITE §4.10.3 Android), epoch-мс.</summary>
    public long ExpiresAtMs { get; init; }

    /// <summary>Откуда адрес: <c>IOS</c>, <c>ANDROID_VR</c>, <c>yt-dlp</c>.</summary>
    public required string Source { get; init; }

    /// <summary>User-Agent, с которым googlevideo отдаёт этот адрес; null — любой.</summary>
    public string? UserAgent { get; init; }

    public double? LoudnessDb { get; init; }
    public long? DurationMs { get; init; }

    public string Codec => MimeType.Contains("opus", StringComparison.Ordinal) ? "Opus" : MimeType.Contains("mp4a", StringComparison.Ordinal) ? "AAC" : MimeType;
}

/// <summary>Класс ошибки получения потока (REWRITE §4.10.3 Android): по нему решаются повторы и текст карточки.</summary>
public enum StreamErrorKind
{
    Network,
    Timeout,
    BotCheck,
    Geo,
    Unavailable,
    Age,
    Extractor,
}

public sealed class StreamException(StreamErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public StreamErrorKind Kind { get; } = kind;

    /// <summary>Сколько раз повторить, прежде чем пропустить трек.</summary>
    public int Retries => Kind switch
    {
        StreamErrorKind.Network => 2,
        StreamErrorKind.Timeout or StreamErrorKind.BotCheck or StreamErrorKind.Extractor => 1,
        _ => 0,
    };
}

/// <summary>Компонент извлечения yt-dlp: путь к исполняемому файлу даёт приложение (оно же его скачивает и обновляет).</summary>
public interface IYtDlpLocator
{
    Task<string?> LocateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Получает поток трека: сначала InnerTube с клиентами, которые отдают прямые ссылки (IOS, затем ANDROID_VR),
/// затем yt-dlp — он переживает изменения YouTube и обновляется сам. Адреса кэшируются (LRU 64) до
/// <c>expire − 5 мин</c>; кэш сбрасывается при 403 и смене сети.
/// </summary>
public sealed class StreamResolver(InnerTubeClient client, IYtDlpLocator? ytDlp = null)
{
    private const int CacheSize = 64;
    private static readonly TimeSpan YtDlpTimeout = TimeSpan.FromSeconds(20);
    private readonly LinkedList<StreamInfo> _cache = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _ytDlpSlots = new(2);

    /// <summary>Порядок источников; тест может поставить свой.</summary>
    public IReadOnlyList<ClientProfile> Clients { get; init; } = [ClientProfile.Ios, ClientProfile.AndroidVr];

    public async Task<StreamInfo> ResolveAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var cached = FromCache(videoId);
        if (cached is not null) return cached;

        StreamException? last = null;
        foreach (var profile in Clients)
        {
            try
            {
                var info = await FromInnerTubeAsync(profile, videoId, cancellationToken).ConfigureAwait(false);
                Remember(info);
                return info;
            }
            catch (StreamException e) when (e.Kind is StreamErrorKind.Geo or StreamErrorKind.Unavailable or StreamErrorKind.Age)
            {
                // YouTube сказал про само видео — другой клиент ответит тем же, но yt-dlp стоит спросить (регион, возраст)
                last = e;
                break;
            }
            catch (StreamException e)
            {
                last = e;
            }
        }

        if (ytDlp is not null && await ytDlp.LocateAsync(cancellationToken).ConfigureAwait(false) is { } exe)
        {
            try
            {
                var info = await FromYtDlpAsync(exe, videoId, cancellationToken).ConfigureAwait(false);
                Remember(info);
                return info;
            }
            catch (StreamException e)
            {
                last = e;
            }
        }
        throw last ?? new StreamException(StreamErrorKind.Extractor, "No stream");
    }

    /// <summary>Забыть адрес трека (403 при чтении): следующий резолв спросит заново.</summary>
    public void Invalidate(string videoId)
    {
        lock (_lock)
        {
            var node = _cache.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.VideoId == videoId) _cache.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>Сеть сменилась: адреса привязаны к IP, все сбрасываются.</summary>
    public void InvalidateAll()
    {
        lock (_lock) _cache.Clear();
    }

    private StreamInfo? FromCache(string videoId)
    {
        lock (_lock)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.VideoId != videoId) continue;
                if (node.Value.ExpiresAtMs <= Domain.IsoTime.NowMs())
                {
                    _cache.Remove(node);
                    return null;
                }
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value;
            }
        }
        return null;
    }

    private void Remember(StreamInfo info)
    {
        lock (_lock)
        {
            var node = _cache.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.VideoId == info.VideoId) _cache.Remove(node);
                node = next;
            }
            _cache.AddFirst(info);
            while (_cache.Count > CacheSize) _cache.RemoveLast();
        }
    }

    private async Task<StreamInfo> FromInnerTubeAsync(ClientProfile profile, string videoId, CancellationToken cancellationToken)
    {
        JsonNode response;
        try
        {
            response = await client.PostAsync(profile, "player",
                new JsonObject { ["videoId"] = videoId, ["contentCheckOk"] = true, ["racyCheckOk"] = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (YouTubeException e)
        {
            throw new StreamException(e.Kind == YouTubeErrorKind.Blocked ? StreamErrorKind.BotCheck : StreamErrorKind.Network, e.Message, e);
        }

        var status = response.Str("playabilityStatus", "status");
        var reason = response.Str("playabilityStatus", "reason") ?? "";
        if (status != "OK") throw new StreamException(Classify(status + " " + reason), $"{profile.Name}: {status} {reason}".Trim());

        var formats = response.Items("streamingData", "adaptiveFormats")
            .Where(f => f.Str("mimeType")?.StartsWith("audio/", StringComparison.Ordinal) == true && f.Str("url") is not null)
            .ToList();
        var chosen = formats.FirstOrDefault(f => f.Long("itag") == 140)
                     ?? formats.Where(f => f.Str("mimeType")!.StartsWith("audio/mp4", StringComparison.Ordinal)).MaxBy(f => f.Long("bitrate") ?? 0)
                     ?? formats.MaxBy(f => f.Long("bitrate") ?? 0);
        if (chosen is null) throw new StreamException(StreamErrorKind.Extractor, $"{profile.Name}: no audio formats with plain URLs");

        var url = chosen.Str("url")!;
        var lengthSeconds = response.Long("videoDetails", "lengthSeconds");
        return new StreamInfo
        {
            VideoId = videoId,
            Url = url,
            Itag = (int)(chosen.Long("itag") ?? 0),
            MimeType = chosen.Str("mimeType") ?? "audio/mp4",
            ContentLength = chosen.Long("contentLength"),
            Bitrate = (int?)chosen.Long("bitrate"),
            ExpiresAtMs = ExpiresAt(url),
            Source = profile.Name,
            UserAgent = profile == ClientProfile.Ios ? null : profile.UserAgent,
            LoudnessDb = response.At("playerConfig", "audioConfig", "loudnessDb") is JsonValue v && v.TryGetValue<double>(out var db) ? db : null,
            DurationMs = lengthSeconds is { } s && s > 0 ? s * 1000 : null,
        };
    }

    /// <summary><c>expire</c> из адреса минус 5 минут; нет параметра — через 5 часов.</summary>
    internal static long ExpiresAt(string url)
    {
        var query = url.IndexOf('?') is var q and >= 0 ? url[(q + 1)..] : "";
        var expire = HttpUtility.ParseQueryString(query)["expire"];
        var now = Domain.IsoTime.NowMs();
        return long.TryParse(expire, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Max(now + 60_000, seconds * 1000 - 5 * 60_000)
            : now + 5 * 3600_000;
    }

    /// <summary>Класс ошибки по тексту YouTube или yt-dlp (таблица REWRITE §4.10.3 Android).</summary>
    public static StreamErrorKind Classify(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("not a bot") || text.Contains("confirm you’re not") || text.Contains("confirm you're not") || text.Contains("sign in to confirm"))
            return StreamErrorKind.BotCheck;
        if (text.Contains("country") || text.Contains("region") || text.Contains("стране") || text.Contains("регион"))
            return StreamErrorKind.Geo;
        if (text.Contains("age") && (text.Contains("confirm") || text.Contains("restricted")) || text.Contains("возраст"))
            return StreamErrorKind.Age;
        if (text.Contains("unavailable") || text.Contains("removed") || text.Contains("private video") || text.Contains("недоступно")
            || text.Contains("error ") && text.Contains("video") || text.StartsWith("error", StringComparison.Ordinal))
            return StreamErrorKind.Unavailable;
        if (text.Contains("unable to download api page") || text.Contains("http error 5") || text.Contains("timed out") || text.Contains("connection"))
            return StreamErrorKind.Network;
        if (text.Contains("login_required") || text.Contains("sign in")) return StreamErrorKind.BotCheck;
        return StreamErrorKind.Extractor;
    }

    private async Task<StreamInfo> FromYtDlpAsync(string exe, string videoId, CancellationToken cancellationToken)
    {
        await _ytDlpSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
                     {
                         "-f", "140/bestaudio[ext=m4a]/bestaudio", "-j", "--no-playlist", "--no-warnings", "--no-progress",
                         "--cache-dir", Path.Combine(Path.GetDirectoryName(exe) ?? ".", "cache"), "--",
                         "https://www.youtube.com/watch?v=" + videoId,
                     })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new StreamException(StreamErrorKind.Extractor, "yt-dlp did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(YtDlpTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new StreamException(StreamErrorKind.Timeout, "yt-dlp timed out");
            }

            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new StreamException(Classify(errors), "yt-dlp: " + errors.Trim().Split('\n').LastOrDefault());

            var json = JsonNode.Parse(output.Trim().Split('\n')[0]);
            var url = json.Str("url") ?? throw new StreamException(StreamErrorKind.Extractor, "yt-dlp gave no url");
            var ext = json.Str("ext");
            var acodec = json.Str("acodec");
            var duration = json.At("duration") is JsonValue d && d.TryGetValue<double>(out var seconds) ? (long?)(seconds * 1000) : null;
            return new StreamInfo
            {
                VideoId = videoId,
                Url = url,
                Itag = int.TryParse(json.Str("format_id"), out var itag) ? itag : 0,
                MimeType = ext == "webm" ? $"audio/webm; codecs=\"{acodec}\"" : $"audio/mp4; codecs=\"{acodec}\"",
                ContentLength = json.Long("filesize") ?? json.Long("filesize_approx"),
                Bitrate = json.At("abr") is JsonValue a && a.TryGetValue<double>(out var abr) ? (int)(abr * 1000) : null,
                ExpiresAtMs = ExpiresAt(url),
                Source = "yt-dlp",
                UserAgent = json.Str("http_headers", "User-Agent"),
                DurationMs = duration,
            };
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new StreamException(StreamErrorKind.Extractor, e.Message, e);
        }
        finally
        {
            _ytDlpSlots.Release();
        }
    }
}
