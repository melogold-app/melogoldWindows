using System.Net;
using System.Net.Http.Headers;

namespace Melogold.Playback;

/// <summary>
/// Чтение диапазонов адреса потока (docs/PROMPT.md §4, грабли §8.1): 403 и истёкший адрес — не пропуск трека, а свежий
/// адрес и повтор: до четырёх раз подряд, со второго — через 1,5, 4 и 8 с. Адрес googlevideo привязан к IP: после смены
/// сети или сервера VPN посреди песни свежий адрес, взятый в ту же секунду, ещё получает 403 — пауза даёт переключению
/// закончиться (пользователь 2026-10-06: песня обрывалась на середине). Сетевая ошибка — повтор через 1 и 3 с. Короткие
/// диапазоны googlevideo не душит.
/// Параллельные чтения (текущий фрагмент и упреждающие) получают 403 на один и тот же истёкший адрес разом: адрес
/// обновляет первое из них, остальные просто повторяют со свежим и не тратят попытки. С <paramref name="cache"/> —
/// сначала кэш песен: что уже на диске, в сеть не ходит, прочитанное из сети ложится туда.
/// </summary>
/// <param name="refreshPauses">паузы перед свежими адресами подряд (тестам — нулевые); их число — сколько адресов пробовать</param>
public sealed class HttpRangeReader(HttpClient http, StreamInfo info, Func<CancellationToken, Task<StreamInfo>> refresh, SongCacheEntry? cache = null,
    IReadOnlyList<TimeSpan>? refreshPauses = null)
{
    private static readonly TimeSpan[] DefaultRefreshPauses = [TimeSpan.Zero, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private readonly IReadOnlyList<TimeSpan> _pauses = refreshPauses ?? DefaultRefreshPauses;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _refreshes;

    public StreamInfo Info { get; private set; } = info;

    /// <summary>Полная длина файла из <c>Content-Range</c> первого ответа.</summary>
    public long? TotalLength { get; private set; } = info.ContentLength;

    public async Task<byte[]> ReadAsync(long start, int length, CancellationToken ct)
    {
        var networkRetries = 0;
        while (true)
        {
            var current = Info;
            var end = start + length - 1;
            if (TotalLength is { } total) end = Math.Min(end, total - 1);
            if (end < start) return [];
            if (cache is not null && cache.TryRead(start, (int)(end - start + 1), out var cached)) return cached;
            if (current.Url.Length == 0)
            {
                // Трек открыт из кэша, а диапазона на диске уже нет (кэш очистили): нужен адрес
                Info = await refresh(ct).ConfigureAwait(false);
                continue;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, current.Url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            if (current.UserAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", current.UserAgent);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
                {
                    await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Адрес уже обновило другое чтение — повторить с ним
                        if (ReferenceEquals(Info, current))
                        {
                            if (_refreshes >= _pauses.Count)
                                throw new StreamException(StreamErrorKind.Extractor,
                                    $"googlevideo {(int)response.StatusCode} after {_pauses.Count} fresh URLs at byte {start} of {TotalLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} ({current.Source})");
                            var pause = _pauses[_refreshes++];
                            if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
                            Info = await refresh(ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _refreshLock.Release();
                    }
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return [];
                // googlevideo тоже считает запросы по адресу: 429 — адрес закрыт, как проверка на бота (tasks/0019)
                if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new StreamException(StreamErrorKind.BotCheck, "googlevideo 429");
                response.EnsureSuccessStatusCode();
                // Свежий адрес работает: следующий 403 (адрес истёк через часы) снова может его обновить
                if (ReferenceEquals(Info, current)) Volatile.Write(ref _refreshes, 0);
                if (response.Content.Headers.ContentRange?.Length is { } full) TotalLength = full;
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                cache?.Write(start, bytes, TotalLength);
                return bytes;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                if (++networkRetries > 2) throw new StreamException(StreamErrorKind.Network, "Network error reading the stream: " + e.Message, e);
                await Task.Delay(networkRetries == 1 ? 1000 : 3000, ct).ConfigureAwait(false);
            }
        }
    }
}
