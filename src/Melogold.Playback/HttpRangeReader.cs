using System.Net;
using System.Net.Http.Headers;

namespace Melogold.Playback;

/// <summary>
/// Чтение диапазонов адреса потока (docs/PROMPT.md §4, грабли §8.1): 403 и истёкший адрес — не пропуск трека, а свежий
/// адрес и повтор (до двух раз подряд); сетевая ошибка — повтор через 1 и 3 с. Короткие диапазоны googlevideo не душит.
/// Параллельные чтения (текущий фрагмент и упреждающие) получают 403 на один и тот же истёкший адрес разом: адрес
/// обновляет первое из них, остальные просто повторяют со свежим и не тратят попытки.
/// </summary>
public sealed class HttpRangeReader(HttpClient http, StreamInfo info, Func<CancellationToken, Task<StreamInfo>> refresh)
{
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
                            if (++_refreshes > 2)
                                throw new StreamException(StreamErrorKind.Extractor, $"googlevideo {(int)response.StatusCode} after fresh URLs");
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
                response.EnsureSuccessStatusCode();
                // Свежий адрес работает: следующий 403 (адрес истёк через часы) снова может его обновить
                if (ReferenceEquals(Info, current)) Volatile.Write(ref _refreshes, 0);
                if (response.Content.Headers.ContentRange?.Length is { } full) TotalLength = full;
                return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                if (++networkRetries > 2) throw new StreamException(StreamErrorKind.Network, "Network error reading the stream: " + e.Message, e);
                await Task.Delay(networkRetries == 1 ? 1000 : 3000, ct).ConfigureAwait(false);
            }
        }
    }
}
