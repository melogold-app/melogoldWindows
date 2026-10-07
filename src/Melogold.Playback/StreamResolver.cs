using System.Globalization;
using System.Web;
using Melogold.Core.Domain;
using Melogold.InnerTube;

namespace Melogold.Playback;

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

    /// <summary>Клиент InnerTube, который дал адрес (для «Сведений о потоке»).</summary>
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

    /// <summary>
    /// Сколько раз повторить, прежде чем пропустить трек: сеть — 2, таймаут и прочее — 1, гео/возраст/недоступно — 0.
    /// Проверка на бота — 0 и без пропуска (tasks/0019): закрытому адресу повтор не поможет, а приблизит блокировку.
    /// </summary>
    public int Retries => Kind switch
    {
        StreamErrorKind.Network => 2,
        StreamErrorKind.Timeout or StreamErrorKind.Extractor => 1,
        _ => 0,
    };

    /// <summary>Пропуск без повторов: причина в самом видео.</summary>
    public bool IsFinal => Kind is StreamErrorKind.Geo or StreamErrorKind.Unavailable or StreamErrorKind.Age;

    /// <summary>Трек закрыт в стране: где YouTube видит устройство (<c>RU</c>); null — не сказал.</summary>
    public string? Country { get; init; }

    /// <summary>В скольких странах правообладатель открыл трек; null — неизвестно.</summary>
    public int? OpenCountries { get; init; }
}

/// <summary>
/// Получает поток трека на чистом C# (решение пользователя, вместо yt-dlp): один запрос InnerTube <c>player</c> на трек,
/// клиентами,
/// которые отдают прямые ссылки без PO-токена (сейчас VISIONOS), по очереди из <see cref="Clients"/> — встроенный список можно заменить свежим из
/// репозитория, не дожидаясь релиза. Формат — itag 140 (AAC в m4a). Адреса кэшируются (LRU 64) до
/// <c>expire − 5 мин</c>; кэш сбрасывается при 403 и смене сети. Одновременно — не больше двух извлечений,
/// у каждого сторож 20 с.
/// <para>
/// Проверка «вы не бот» (tasks/0019): YouTube считает гостевые запросы по адресу, и за одним сервером VPN сидит много
/// людей — каждый лишний запрос приближает блокировку для всех. Поэтому на неё — ни следующего клиента, ни диагноза, ни
/// повтора, а адрес помнится закрытым 10 минут: заготовка, загрузки и обновление адреса в YouTube не ходят, действие
/// человека пробует один запрос. Удача, смена сети и истечение 10 минут снимают отметку.
/// </para>
/// </summary>
/// <param name="log">строка в журнал на каждый отказ с диагнозом (задание 0010)</param>
public sealed class StreamResolver(InnerTubeClient client, Action<string>? log = null)
{
    private const int CacheSize = 64;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);
    private readonly LinkedList<StreamInfo> _cache = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _slots = new(2);

#if DEBUG
    /// <summary>
    /// Только отладочная сборка: <c>MELOGOLD_FAKE_GEO=&lt;videoId&gt;:&lt;страна&gt;</c> — поток этого трека «не получен», а
    /// YouTube «видит» устройство в этой стране. Чтобы увидеть карточку задания 0010, находясь не в России.
    /// </summary>
    private static readonly string[]? FakeGeo = Environment.GetEnvironmentVariable("MELOGOLD_FAKE_GEO")?.Split(':');
#endif

    /// <summary>Порядок клиентов; <see cref="StreamClients"/> подменяет его свежим списком.</summary>
    public IReadOnlyList<ClientProfile> Clients { get; set; } = StreamClients.BuiltIn;

    /// <summary>Сколько помнить, что YouTube не пускает адрес.</summary>
    public static readonly TimeSpan BlockMemory = TimeSpan.FromMinutes(10);

    private long _blockedUntil;

    /// <summary>Часы для отметки «адрес закрыт» (тестам — свои).</summary>
    public Func<long> Clock { get; set; } = IsoTime.NowMs;

    /// <summary>YouTube недавно спросил «вы не бот» с этого адреса.</summary>
    public bool IsBlocked => Volatile.Read(ref _blockedUntil) > Clock();

    /// <summary>Отметка снята удачей или сменой сети: ждавшие загрузки идут дальше.</summary>
    public event Action? Unblocked;

    /// <summary>Адрес закрыт: и по ответу <c>player</c>, и по 429 от googlevideo.</summary>
    public void MarkBlocked()
    {
        if (Interlocked.Exchange(ref _blockedUntil, Clock() + (long)BlockMemory.TotalMilliseconds) <= Clock())
            log?.Invoke("YouTube asks to confirm we are not a bot: no stream requests but the user's for 10 minutes");
    }

    private void ClearBlock()
    {
        if (Interlocked.Exchange(ref _blockedUntil, 0) != 0) Unblocked?.Invoke();
    }

    private StreamException Blocked(string videoId) => new(StreamErrorKind.BotCheck, $"{videoId}: this address is blocked by YouTube (remembered), not asked");

    /// <param name="probe">
    /// действие человека (нажатие на трек, «Повторить», «Далее»): при закрытом адресе — один пробный запрос; без него
    /// (заготовка, загрузки, обновление адреса) при закрытом адресе запроса нет
    /// </param>
    public async Task<StreamInfo> ResolveAsync(string videoId, CancellationToken cancellationToken = default, bool probe = false)
    {
        if (FromCache(videoId) is { } cached) return cached;
        if (!probe && IsBlocked) throw Blocked(videoId);

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FromCache(videoId) is { } again) return again;
            if (!probe && IsBlocked) throw Blocked(videoId);
            StreamException? last = null;
            foreach (var profile in Clients)
            {
                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                watchdog.CancelAfter(Watchdog);
                try
                {
                    var info = await FromClientAsync(profile, videoId, watchdog.Token).ConfigureAwait(false);
                    Remember(info);
                    ClearBlock();
                    return info;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    last = new StreamException(StreamErrorKind.Timeout, $"{profile.Name}: timeout");
                }
                catch (StreamException e) when (e.Kind == StreamErrorKind.BotCheck)
                {
                    // Закрытому адресу не помогут ни другой клиент, ни диагноз: каждый запрос — ещё шаг к блокировке
                    MarkBlocked();
                    log?.Invoke($"Stream of {videoId} not played: BotCheck; nothing more asked; client: {e.Message}");
                    throw;
                }
                catch (StreamException e) when (e.IsFinal)
                {
                    // Причина в самом видео: другой клиент ответит тем же
                    throw await ExplainAsync(videoId, e, cancellationToken).ConfigureAwait(false);
                }
                catch (StreamException e)
                {
                    last = e;
                }
            }
            throw await ExplainAsync(videoId, last ?? new StreamException(StreamErrorKind.Extractor, "No stream clients"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Забыть адрес трека (403 при чтении): следующий резолв спросит заново.</summary>
    public void Invalidate(string videoId)
    {
        lock (_lock)
        {
            for (var node = _cache.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.VideoId == videoId) _cache.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>
    /// Новый сеанс YouTube перед свежим адресом (403 при чтении): забыть <c>visitorData</c> и все адреса. «Помеченному»
    /// сеансу googlevideo отдаёт только первый мегабайт, дальше — 403, и так на каждом свежем адресе того же сеанса
    /// (07.10.2026, Linux, задание 0025: три свежих адреса сеанса — 403 403 403, три с новым <c>visitorData</c> —
    /// 206 206 206). Истёкшему адресу новый сеанс не мешает: один лёгкий запрос лишний.
    /// </summary>
    public void RenewSession()
    {
        client.VisitorData = null;
        lock (_lock) _cache.Clear();
        log?.Invoke("Stream URL refused: next URL with a fresh visitorData");
    }

    /// <summary>Сеть сменилась: адреса привязаны к IP, сбрасываются все; новый адрес может быть и не закрыт.</summary>
    public void InvalidateAll()
    {
        lock (_lock) _cache.Clear();
        ClearBlock();
    }

    private StreamInfo? FromCache(string videoId)
    {
        lock (_lock)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.VideoId != videoId) continue;
                _cache.Remove(node);
                if (node.Value.ExpiresAtMs <= IsoTime.NowMs()) return null;
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
            for (var node = _cache.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.VideoId == info.VideoId) _cache.Remove(node);
                node = next;
            }
            _cache.AddFirst(info);
            while (_cache.Count > CacheSize) _cache.RemoveLast();
        }
    }

    private async Task<StreamInfo> FromClientAsync(ClientProfile profile, string videoId, CancellationToken ct)
    {
#if DEBUG
        if (FakeGeo is [var fakeId, _] && fakeId == videoId) throw new StreamException(StreamErrorKind.Unavailable, $"{profile.Name}: UNPLAYABLE Video unavailable (MELOGOLD_FAKE_GEO)");
#endif
        PlayerResponse response;
        try
        {
            await client.EnsureVisitorDataAsync(ct).ConfigureAwait(false);
            response = await client.PlayerAsync(profile, videoId, ct).ConfigureAwait(false);
        }
        catch (YouTubeException e)
        {
            throw new StreamException(e.Kind == YouTubeErrorKind.Blocked ? StreamErrorKind.BotCheck : StreamErrorKind.Network, e.Message, e);
        }
        if (response.Status != "OK")
            throw new StreamException(Classify(response.Status + " " + response.Reason), $"{profile.Name}: {response.Status} {response.Reason}".Trim());

        var formats = response.AudioFormats;
        var chosen = formats.FirstOrDefault(f => f.Itag == 140)
                     ?? formats.Where(f => f.MimeType.StartsWith("audio/mp4", StringComparison.Ordinal)).MaxBy(f => f.Bitrate ?? 0)
                     ?? formats.MaxBy(f => f.Bitrate ?? 0);
        if (chosen is null) throw new StreamException(StreamErrorKind.Extractor, $"{profile.Name}: no audio with a plain URL");

        return new StreamInfo
        {
            VideoId = videoId,
            Url = chosen.Url,
            Itag = chosen.Itag,
            MimeType = chosen.MimeType,
            ContentLength = chosen.ContentLength,
            Bitrate = chosen.Bitrate,
            ExpiresAtMs = ExpiresAt(chosen.Url),
            Source = profile.Name,
            UserAgent = profile.MediaUserAgent,
            LoudnessDb = response.LoudnessDb,
            DurationMs = response.DurationMs,
        };
    }

    /// <summary><c>expire</c> из адреса минус 5 минут; нет параметра — через 5 часов.</summary>
    public static long ExpiresAt(string url)
    {
        var query = url.IndexOf('?') is var q and >= 0 ? url[(q + 1)..] : "";
        var expire = HttpUtility.ParseQueryString(query)["expire"];
        var now = IsoTime.NowMs();
        return long.TryParse(expire, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Max(now + 60_000, seconds * 1000 - 5 * 60_000)
            : now + 5 * 3600_000;
    }

    /// <summary>
    /// Поток не получен: один раз спросить YouTube клиентом WEB, почему (<see cref="PlayabilityRequest.PlayabilityAsync"/>),
    /// и уточнить ошибку — трек закрыт в стране (со страной и числом стран), возраст, удалён. Сеть и таймаут не
    /// уточняются: YouTube тогда не ответит и WEB. В журнал — одна строка на отказ.
    /// </summary>
    private async Task<StreamException> ExplainAsync(string videoId, StreamException failure, CancellationToken ct)
    {
        if (failure.Kind is StreamErrorKind.Network or StreamErrorKind.Timeout) return failure;
        Playability? playability = null;
        try
        {
            playability = await client.PlayabilityAsync(videoId, ct).ConfigureAwait(false);
#if DEBUG
            if (FakeGeo is [var fakeId, var country] && fakeId == videoId) playability = playability with { Country = country };
#endif
        }
        catch (YouTubeException)
        {
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        var result = Diagnose(playability, failure) ?? failure;
        log?.Invoke(FormattableString.Invariant(
            $"Stream of {videoId} not played: {result.Kind}; YouTube {playability?.Status ?? "-"} \"{playability?.Reason}\", country {playability?.Country ?? "-"}, open in {playability?.AvailableCountries.Count.ToString(CultureInfo.InvariantCulture) ?? "-"}; client: {failure.Message}"));
        return result;
    }

    private static readonly string[] GeoPhrases = ["available in your country", "not made this video available in your country", "blocked it in your country"];
    private static readonly string[] AgePhrases = ["confirm your age", "age-restricted", "inappropriate for some users"];
    private static readonly string[] GonePhrases = ["Private video", "has been removed", "account associated with this video has been terminated", "no longer available"];

    /// <summary>
    /// Итог диагноза (задание 0010 §2.3, Android <c>Unavailability.kt</c>), по порядку: страна вне списка открытых — закрыт
    /// в стране (страна и число); фразы о стране — закрыт в стране; о возрасте — возраст; об удалении — удалён. null —
    /// диагноз ничего не добавил, остаётся прежняя ошибка.
    /// </summary>
    public static StreamException? Diagnose(Playability? playability, StreamException failure)
    {
        var reason = playability?.Reason ?? "";
        bool Says(string[] phrases) => phrases.Any(p => failure.Message.Contains(p, StringComparison.OrdinalIgnoreCase) || reason.Contains(p, StringComparison.OrdinalIgnoreCase));
        var open = playability?.AvailableCountries.Count is > 0 and var count ? count : (int?)null;
        if (playability is { IsBlockedHere: true })
            return new StreamException(StreamErrorKind.Geo, $"Closed in {playability.Country}, open in {open} countries", failure) { Country = playability.Country, OpenCountries = open };
        if (Says(GeoPhrases))
            return new StreamException(StreamErrorKind.Geo, "Closed in the country: " + (reason.Length > 0 ? reason : failure.Message), failure) { Country = playability?.Country, OpenCountries = open };
        if (Says(AgePhrases)) return new StreamException(StreamErrorKind.Age, "Age check: " + (reason.Length > 0 ? reason : failure.Message), failure);
        if (Says(GonePhrases)) return new StreamException(StreamErrorKind.Unavailable, "Removed or private: " + (reason.Length > 0 ? reason : failure.Message), failure);
        return null;
    }

    /// <summary>Класс ошибки по тексту YouTube (таблица REWRITE §4.10.3 Android).</summary>
    public static StreamErrorKind Classify(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("not a bot") || text.Contains("confirm you’re not") || text.Contains("confirm you're not") || text.Contains("sign in to confirm"))
            return StreamErrorKind.BotCheck;
        if (text.Contains("country") || text.Contains("region") || text.Contains("стране") || text.Contains("регион"))
            return StreamErrorKind.Geo;
        if ((text.Contains("age") && (text.Contains("confirm") || text.Contains("restricted"))) || text.Contains("возраст"))
            return StreamErrorKind.Age;
        if (text.Contains("unavailable") || text.Contains("removed") || text.Contains("private") || text.Contains("недоступно")
            || text.StartsWith("error", StringComparison.Ordinal) || text.StartsWith("unplayable", StringComparison.Ordinal))
            return StreamErrorKind.Unavailable;
        if (text.Contains("login_required") || text.Contains("sign in")) return StreamErrorKind.BotCheck;
        return StreamErrorKind.Extractor;
    }
}
