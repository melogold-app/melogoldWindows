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

    /// <summary>Ждёт: YouTube не пускает адрес (проверка на бота, tasks/0019) — не сбой, скачанное остаётся.</summary>
    Waiting,
}

/// <summary>Как идёт загрузка трека: состояние и доля скачанного (0…1), если известна.</summary>
public readonly record struct DownloadState(DownloadStatus Status, double? Progress);

/// <summary>
/// Загрузки (tasks/0003 §2, Android <c>Downloads</c>): «Скачать» оставляет трек насовсем — в своей папке того же формата,
/// что кэш музыки, которую ни лимит, ни «Очистить кэш» не трогают. Трек, целиком лежащий в кэше, копируется сразу и
/// без сети; остальные качаются кусками по диапазонам (одним запросом googlevideo душит скорость), по два сразу, с долей
/// скачанного. Список загрузок — в библиотеке (<see cref="Library.DownloadIds"/>): прерванные продолжаются при запуске.
/// <para>
/// Проверка на бота (tasks/0019) не попытка и не сбой: вся очередь ждёт с причиной, порядок и скачанные куски остаются.
/// Дальше — по «Возобновить», при смене сети или удаче плеера, и первым идёт один пробный запрос.
/// </para>
/// </summary>
public sealed class TrackDownloads
{
    private const int Chunk = 1024 * 1024;
    private const int Parallel = 2;

    private readonly SongCache store;
    private readonly SongCache player;
    private readonly StreamResolver resolver;
    private readonly Library library;
    private readonly Action<string, Exception?> log;
    private readonly Lock _gate = new();

    /// <summary>Очередь ждёт, пока адрес закрыт; завершается, когда можно пробовать.</summary>
    private TaskCompletionSource? _waiting;

    /// <summary>После ожидания: пока пробный запрос не прошёл, остальные ждут его итога.</summary>
    private TaskCompletionSource? _probe;

    /// <summary>Трек, который делает пробный запрос.</summary>
    private string? _prober;

    public TrackDownloads(SongCache store, SongCache player, StreamResolver resolver, Library library, Action<string, Exception?> log)
    {
        this.store = store;
        this.player = player;
        this.resolver = resolver;
        this.library = library;
        this.log = log;
        // Сменилась сеть или плеер получил поток — адрес снова пускают
        resolver.Unblocked += ResumeWaiting;
    }

    /// <summary>Очередь ждёт: YouTube не пускает адрес.</summary>
    public bool IsWaiting
    {
        get
        {
            lock (_gate) return _waiting is not null;
        }
    }

    /// <summary>Очередь встала в ожидание или пошла дальше — плашка «Загрузки ждут» и кнопка «Возобновить».</summary>
    public event Action? WaitingChanged;

    private readonly ConcurrentDictionary<string, (DownloadState State, CancellationTokenSource Cancel)> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(Parallel, Parallel);

    /// <summary>
    /// Адреса — по одному: если YouTube спросит «вы не бот», вторая загрузка узнает это из отметки, а не своим запросом.
    /// </summary>
    private readonly SemaphoreSlim _resolving = new(1, 1);
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

    /// <summary>«Скачать снова» после сбоя; у ждущей очереди — «Возобновить».</summary>
    public void Retry(string videoId)
    {
        if (State(videoId)?.Status == DownloadStatus.Waiting) ResumeWaiting();
        else Start(videoId);
    }

    /// <summary>«Возобновить»: ждущая очередь идёт дальше — сначала один пробный запрос.</summary>
    public void ResumeWaiting()
    {
        TaskCompletionSource? waiting;
        lock (_gate)
        {
            waiting = _waiting;
            if (waiting is null) return;
            _waiting = null;
            _probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _prober = null;
        }
        log("Downloads resume: one probe first", null);
        waiting.TrySetResult();
        WaitingChanged?.Invoke();
    }

    /// <summary>Проверка на бота: вся очередь ждёт, ждавшие пробу — тоже, запросов они не делают.</summary>
    private void Wait()
    {
        TaskCompletionSource? probe;
        bool started;
        lock (_gate)
        {
            probe = _probe;
            _probe = null;
            _prober = null;
            started = _waiting is null;
            _waiting ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        probe?.TrySetResult();
        if (!started) return;
        log("Downloads wait: YouTube blocks this address", null);
        WaitingChanged?.Invoke();
    }

    /// <summary>
    /// Можно ли идти в YouTube: очередь не ждёт; после ожидания первый — пробный, остальные ждут его итога. true — этот
    /// трек и есть проба.
    /// </summary>
    private async Task<bool> TurnAsync(string videoId, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_waiting is { } waiting) wait = waiting.Task;
                else if (_probe is not { } probe) return false;
                else if (_prober is null || _prober == videoId)
                {
                    _prober = videoId;
                    return true;
                }
                else wait = probe.Task;
            }
            SetState(videoId, new DownloadState(DownloadStatus.Waiting, null));
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Итог пробы: прошла — очередь идёт как обычно. Не прошла без проверки на бота (отменили, сбой сети, адрес дал
    /// кэш, а отметка осталась) — пробует следующий в очереди.
    /// </summary>
    private void ProbeDone(string videoId, bool passed)
    {
        TaskCompletionSource? done;
        lock (_gate)
        {
            if (_prober != videoId) return;
            done = _probe;
            _prober = null;
            _probe = passed ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done?.TrySetResult();
    }

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
        var probing = false;
        try
        {
            while (true)
            {
                var slot = false;
                try
                {
                    // Целиком в кэше плеера — копия без сети, очереди ждать не надо
                    if (!store.IsComplete(videoId) && !store.CopyFrom(player, videoId))
                    {
                        probing = await TurnAsync(videoId, ct).ConfigureAwait(false);
                        await _slots.WaitAsync(ct).ConfigureAwait(false);
                        slot = true;
                        SetState(videoId, new DownloadState(DownloadStatus.Queued, null));
                        await FetchAsync(videoId, store, true, ct, probing).ConfigureAwait(false);
                    }
                    if (probing)
                    {
                        ProbeDone(videoId, passed: !resolver.IsBlocked);
                        probing = false;
                    }
                    if (!store.IsComplete(videoId)) throw new IOException("the download is not complete");
                    // Дубль в кэше плеера больше не нужен: место — новым трекам
                    player.Remove(videoId, unlessPlaying: true);
                    log($"Downloaded {videoId}", null);
                    return;
                }
                catch (StreamException e) when (e.Kind == StreamErrorKind.BotCheck && !ct.IsCancellationRequested)
                {
                    // Не попытка и не сбой: вся очередь ждёт, скачанные куски остаются
                    probing = false;
                    resolver.MarkBlocked();
                    Wait();
                    SetState(videoId, new DownloadState(DownloadStatus.Waiting, null));
                }
                finally
                {
                    if (slot) _slots.Release();
                }
            }
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
            if (probing) ProbeDone(videoId, passed: false);
            if (!ct.IsCancellationRequested)
            {
                _active.TryRemove(videoId, out _);
                Changed?.Invoke(videoId);
            }
        }
    }

    private void SetState(string videoId, DownloadState state)
    {
        if (!_active.TryGetValue(videoId, out var active) || active.State == state) return;
        _active[videoId] = (state, active.Cancel);
        Changed?.Invoke(videoId);
    }

    /// <summary>
    /// Все байты трека (для «Сохранить файлом»): из загрузок, из кэша или — если его нет целиком нигде — из сети в кэш
    /// музыки (заодно он будет играть без сети).
    /// </summary>
    public async Task<byte[]> ReadWholeAsync(string videoId, CancellationToken ct = default)
    {
        if (store.ReadComplete(videoId) is { } downloaded) return downloaded;
        if (player.ReadComplete(videoId) is { } cached) return cached;
        // «Сохранить файлом» нажал человек: при закрытом адресе — один пробный запрос
        await FetchAsync(videoId, player, false, ct, probe: true).ConfigureAwait(false);
        return player.ReadComplete(videoId) ?? throw new IOException("the track is not complete");
    }

    /// <summary>Весь поток — кусками в <paramref name="target"/>; что уже на диске (прерванная загрузка), в сеть не ходит.</summary>
    private async Task FetchAsync(string videoId, SongCache target, bool report, CancellationToken ct, bool probe = false)
    {
        var info = await ResolveAsync(videoId, ct, probe).ConfigureAwait(false);
        var entry = target.Entry(info);
        try
        {
            var reader = new HttpRangeReader(_http, info, async token =>
            {
                resolver.Invalidate(videoId);
                return await ResolveAsync(videoId, token, false).ConfigureAwait(false);
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

    private async Task<StreamInfo> ResolveAsync(string videoId, CancellationToken ct, bool probe)
    {
        await _resolving.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await resolver.ResolveAsync(videoId, ct, probe).ConfigureAwait(false);
        }
        finally
        {
            _resolving.Release();
        }
    }

    private void Report(string videoId, double? progress)
    {
        if (!_active.TryGetValue(videoId, out var active)) return;
        _active[videoId] = (new DownloadState(DownloadStatus.Downloading, progress), active.Cancel);
        Changed?.Invoke(videoId);
    }
}
