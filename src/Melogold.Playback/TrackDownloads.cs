using System.Collections.Concurrent;
using Melogold.Core.Data;
using Melogold.Core.Music;

namespace Melogold.Playback;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
}

/// <summary>Как идёт загрузка трека: состояние и доля скачанного (0…1), если известна.</summary>
public readonly record struct DownloadState(DownloadStatus Status, double? Progress);

/// <summary>
/// Загрузки (tasks/0003 §2, Android <c>Downloads</c>): «Скачать» оставляет трек насовсем — в своей папке того же формата,
/// что кэш музыки, которую ни лимит, ни «Очистить кэш» не трогают. Трек, целиком лежащий в кэше, копируется сразу и
/// без сети; остальные качаются кусками по диапазонам (одним запросом googlevideo душит скорость), по два сразу, с долей
/// скачанного. Список загрузок — в библиотеке (<see cref="Library.DownloadIds"/>): прерванные продолжаются при запуске.
/// </summary>
public sealed class TrackDownloads(SongCache store, SongCache player, StreamResolver resolver, Library library, Action<string, Exception?> log)
{
    private const int Chunk = 1024 * 1024;
    private const int Parallel = 2;

    private readonly ConcurrentDictionary<string, (DownloadState State, CancellationTokenSource Cancel)> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(Parallel, Parallel);
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Скачанное лежит здесь: плеер играет отсюда без сети.</summary>
    public SongCache Store => store;

    /// <summary>У трека изменилась загрузка (началась, продвинулась, закончилась, удалена) — метки в строках обновляются.</summary>
    public event Action<string>? Changed;

    /// <summary>Загрузка трека; null — трек не скачивали.</summary>
    public DownloadState? State(string videoId)
    {
        if (_active.TryGetValue(videoId, out var active)) return active.State;
        if (store.IsComplete(videoId)) return new DownloadState(DownloadStatus.Completed, 1);
        return _failed.ContainsKey(videoId) ? new DownloadState(DownloadStatus.Failed, null) : null;
    }

    public bool IsDownloaded(string videoId) => store.IsComplete(videoId);

    /// <summary>Сколько занимают загрузки.</summary>
    public long Size => store.Size;

    /// <summary>«Скачать»: трек — в список загрузок и в очередь.</summary>
    public void Download(Track track)
    {
        library.AddDownload(track);
        Start(track.VideoId);
    }

    /// <summary>«Скачать снова» после сбоя.</summary>
    public void Retry(string videoId) => Start(videoId);

    /// <summary>При запуске: то, что скачивали и не докачали, продолжается.</summary>
    public void Resume()
    {
        foreach (var videoId in library.DownloadIds())
            if (!store.IsComplete(videoId)) Start(videoId);
    }

    /// <summary>«Отменить загрузку» и «Удалить загрузку»: из списка, байты — с диска.</summary>
    public void Remove(string videoId)
    {
        if (_active.TryRemove(videoId, out var active)) active.Cancel.Cancel();
        _failed.TryRemove(videoId, out _);
        library.RemoveDownload(videoId);
        store.Remove(videoId);
        Changed?.Invoke(videoId);
    }

    /// <summary>«Удалить все загрузки» (Настройки › Хранилище).</summary>
    public void RemoveAll()
    {
        foreach (var videoId in library.DownloadIds().Concat(store.VideoIds()).Concat(_active.Keys).Distinct().ToList()) Remove(videoId);
        library.RemoveAllDownloads();
    }

    private void Start(string videoId)
    {
        var cancel = new CancellationTokenSource();
        if (!_active.TryAdd(videoId, (new DownloadState(DownloadStatus.Queued, null), cancel))) return;
        _failed.TryRemove(videoId, out _);
        Changed?.Invoke(videoId);
        _ = Task.Run(() => RunAsync(videoId, cancel.Token));
    }

    private async Task RunAsync(string videoId, CancellationToken ct)
    {
        var slot = false;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            slot = true;
            if (!store.IsComplete(videoId) && !store.CopyFrom(player, videoId)) await FetchAsync(videoId, store, true, ct).ConfigureAwait(false);
            if (!store.IsComplete(videoId)) throw new IOException("the download is not complete");
            // Дубль в кэше плеера больше не нужен: место — новым трекам
            player.Remove(videoId, unlessPlaying: true);
            log($"Downloaded {videoId}", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            log($"Download of {videoId} failed", e);
            _failed[videoId] = true;
        }
        finally
        {
            if (slot) _slots.Release();
            if (!ct.IsCancellationRequested)
            {
                _active.TryRemove(videoId, out _);
                Changed?.Invoke(videoId);
            }
        }
    }

    /// <summary>
    /// Все байты трека (для «Сохранить файлом»): из загрузок, из кэша или — если его нет целиком нигде — из сети в кэш
    /// музыки (заодно он будет играть без сети).
    /// </summary>
    public async Task<byte[]> ReadWholeAsync(string videoId, CancellationToken ct = default)
    {
        if (store.ReadComplete(videoId) is { } downloaded) return downloaded;
        if (player.ReadComplete(videoId) is { } cached) return cached;
        await FetchAsync(videoId, player, false, ct).ConfigureAwait(false);
        return player.ReadComplete(videoId) ?? throw new IOException("the track is not complete");
    }

    /// <summary>Весь поток — кусками в <paramref name="target"/>; что уже на диске (прерванная загрузка), в сеть не ходит.</summary>
    private async Task FetchAsync(string videoId, SongCache target, bool report, CancellationToken ct)
    {
        var info = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);
        var entry = target.Entry(info);
        try
        {
            var reader = new HttpRangeReader(_http, info, async token =>
            {
                resolver.Invalidate(videoId);
                return await resolver.ResolveAsync(videoId, token).ConfigureAwait(false);
            }, entry);
            long position = 0;
            var reported = DateTime.MinValue;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await reader.ReadAsync(position, Chunk, ct).ConfigureAwait(false);
                if (bytes.Length == 0) break;
                position += bytes.Length;
                var total = reader.TotalLength;
                if (total is { } length && position >= length) break;
                if (report && DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(300))
                {
                    reported = DateTime.UtcNow;
                    Report(videoId, total is > 0 ? (double)position / total.Value : null);
                }
            }
        }
        finally
        {
            entry.Release();
        }
    }

    private void Report(string videoId, double? progress)
    {
        if (!_active.TryGetValue(videoId, out var active)) return;
        _active[videoId] = (new DownloadState(DownloadStatus.Downloading, progress), active.Cancel);
        Changed?.Invoke(videoId);
    }
}
