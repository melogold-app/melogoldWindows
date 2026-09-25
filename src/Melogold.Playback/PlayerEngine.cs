using System.Diagnostics;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Melogold.Playback;

/// <summary>Настройки, которые читает плеер (живут в приложении).</summary>
public interface IPlaybackSettings
{
    double Volume { get; }
    bool Muted { get; }
    double Speed { get; }
    bool NormalizeVolume { get; }
    bool Autoplay { get; }
    bool PauseHistory { get; }
}

/// <summary>Что показывает панель воспроизведения.</summary>
public enum PlayerStatus
{
    Idle,

    /// <summary>Получаем поток: на кнопке play — кольцо, через 3 с подпись «Получаем поток…».</summary>
    Resolving,

    Buffering,
    Playing,
    Paused,

    /// <summary>Остановлено с ошибкой: причина словами и «Повторить».</summary>
    Error,
}

/// <summary>Причина ошибки для текста (REWRITE §3.10.9 Android).</summary>
public sealed record PlayerError(StreamErrorKind Kind, string Message, Track Track);

/// <summary>
/// Плеер (docs/PROMPT.md §4): очередь <see cref="PlayQueue"/>, поток через <see cref="StreamResolver"/> и
/// <see cref="AacStreamSource"/> (кадры AAC из DASH-фрагментов в <see cref="MediaStreamSource"/>), <see cref="MediaPlayer"/> с системными медиаклавишами и плашкой (SMTC).
/// Ошибки — по классам: сеть — 2 повтора, таймаут и бот — 1, гео/недоступно/возраст — сразу пропуск; после трёх
/// пропусков подряд воспроизведение останавливается. Все публичные методы и события — в потоке интерфейса
/// (<see cref="SynchronizationContext"/> создателя).
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    private const int MinPlayMs = 5000;
    private readonly MediaPlayer _player;
    private readonly StreamResolver _resolver;
    private readonly YouTubeMusic _music;
    private readonly Library _library;
    private readonly IPlaybackSettings _settings;
    private readonly HttpClient _http;
    private readonly SynchronizationContext _ui;
    private readonly HashSet<string> _playedThisSession = [];
    private CancellationTokenSource? _load;

    /// <summary>Заранее открытое начало звука следующего трека: переход по очереди — без ожидания сети.</summary>
    private readonly Dictionary<string, Task<AacStreamSource>> _preloaded = [];
    private AacStreamSource? _stream;
    private int _skipsInRow;
    private bool _autoplayLoading;
    private int _autoplaySeedsWithoutNew;
    private string? _autoplayContinuation;
    private string? _autoplayPlaylistId;
    private string? _autoplaySeed;

    // Учёт прослушивания текущего элемента: реальное время звучания
    private readonly Stopwatch _listened = new();
    private Track? _listenedTrack;

    public PlayerEngine(StreamResolver resolver, YouTubeMusic music, Library library, IPlaybackSettings settings)
    {
        _resolver = resolver;
        _music = music;
        _library = library;
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        // googlevideo — по HTTP/2: куски и соседние фрагменты идут по одному соединению, без новых TLS-рукопожатий
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
        };
        _player.CommandManager.IsEnabled = true;
        _player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.NextReceived += (_, e) =>
        {
            e.Handled = true;
            Post(() => Next());
        };
        _player.CommandManager.PreviousReceived += (_, e) =>
        {
            e.Handled = true;
            Post(Previous);
        };
        _player.MediaEnded += (_, _) => Post(OnEnded);
        _player.MediaFailed += (_, e) => Post(() => OnFailed(e));
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => Post(OnSessionStateChanged);
        Queue.Changed += () => QueueChanged?.Invoke();
        ApplyVolume();
    }

    public PlayQueue Queue { get; } = new();

    public PlayerStatus Status { get; private set; } = PlayerStatus.Idle;

    public Track? Current => Queue.CurrentItem?.Track;

    public PlayerError? Error { get; private set; }

    /// <summary>Поток текущего трека (для «Сведений о потоке»).</summary>
    public StreamInfo? Stream => _stream?.Info;

    public TimeSpan Position => _player.PlaybackSession.Position;

    /// <summary>Громкость на выходе (с нормализацией), 0 — без звука: уровни захвата звука делятся на неё.</summary>
    public double OutputVolume => _player.IsMuted ? 0 : _player.Volume;

    public TimeSpan Duration
    {
        get
        {
            var natural = _player.PlaybackSession.NaturalDuration;
            if (natural > TimeSpan.Zero) return natural;
            return Current?.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    public bool IsPlaying => Status is PlayerStatus.Playing or PlayerStatus.Buffering || (Status == PlayerStatus.Resolving && _playWhenReady);

    private bool _playWhenReady;

    public event Action? StateChanged;

    public event Action? TrackChanged;

    public event Action? QueueChanged;

    /// <summary>Трек пропущен из-за ошибки: для снекбара «Пропущен „…“: причина».</summary>
    public event Action<PlayerError>? Skipped;

    private void Post(Action action) => _ui.Post(_ => action(), null);

    private void SetStatus(PlayerStatus status)
    {
        if (Status == status) return;
        Status = status;
        UpdateListening();
        StateChanged?.Invoke();
    }

    // ---------- Команды ----------

    /// <summary>Играть список с выбранного трека (REWRITE §2.3: тап в альбоме, плейлисте, Избранном…).</summary>
    public void PlayList(IReadOnlyList<Track> tracks, int startIndex, bool shuffle = false)
    {
        if (tracks.Count == 0) return;
        FinishListening();
        ResetAutoplay();
        Queue.SetList(tracks, startIndex, shuffle);
        _ = LoadCurrentAsync(play: true);
    }

    /// <summary>Одиночный трек и дальше автовоспроизведение похожих (поиск, «Недавние», ссылка).</summary>
    public void PlaySingle(Track track, long startMs = 0)
    {
        FinishListening();
        ResetAutoplay();
        Queue.SetSingle(track);
        _ = LoadCurrentAsync(play: true, startMs);
    }

    /// <summary>«Включить радио»: трек и блок автовоспроизведения.</summary>
    public void StartRadio(Track track) => PlaySingle(track);

    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        var wasEmpty = Queue.CurrentItem is null;
        Queue.PlayNext(tracks);
        if (wasEmpty) _ = LoadCurrentAsync(play: true);
        else PrefetchUpcoming();
    }

    public void AddToEnd(IReadOnlyList<Track> tracks)
    {
        var wasEmpty = Queue.CurrentItem is null;
        Queue.AddToEnd(tracks);
        if (wasEmpty) _ = LoadCurrentAsync(play: true);
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Play()
    {
        if (Current is null) return;
        if (Status is PlayerStatus.Error or PlayerStatus.Idle || _player.Source is null)
        {
            _ = LoadCurrentAsync(play: true, (long)Position.TotalMilliseconds);
            return;
        }
        _playWhenReady = true;
        _player.Play();
    }

    public void Pause()
    {
        _playWhenReady = false;
        _player.Pause();
        if (Status == PlayerStatus.Resolving) StateChanged?.Invoke();
    }

    public void Next(bool userAction = true)
    {
        FinishListening();
        _skipsInRow = 0;
        if (!Queue.MoveNext(userAction))
        {
            // Конец очереди без автовоспроизведения: остановка на последнем треке в позиции 0
            _player.Pause();
            _player.PlaybackSession.Position = TimeSpan.Zero;
            SetStatus(PlayerStatus.Paused);
            return;
        }
        _ = LoadCurrentAsync(play: true);
    }

    /// <summary>«Предыдущий»: с позиции больше 3 с — в начало трека (REWRITE §4.10.4).</summary>
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3) || !Queue.MovePrevious())
        {
            Seek(TimeSpan.Zero);
            return;
        }
        FinishListening();
        _ = LoadCurrentAsync(play: true);
    }

    public void JumpTo(long queueItemId)
    {
        FinishListening();
        if (Queue.JumpTo(queueItemId)) _ = LoadCurrentAsync(play: true);
    }

    public void Seek(TimeSpan position)
    {
        if (_player.PlaybackSession.CanSeek) _player.PlaybackSession.Position = position;
        StateChanged?.Invoke();
    }

    public void SetRepeat(RepeatMode mode)
    {
        Queue.SetRepeat(mode);
        StateChanged?.Invoke();
    }

    public void SetShuffle(bool on)
    {
        Queue.Shuffle(on);
        PrefetchUpcoming();
        StateChanged?.Invoke();
    }

    public void Retry() => _ = LoadCurrentAsync(play: true, (long)Position.TotalMilliseconds);

    /// <summary>Громкость, «без звука», скорость и нормализация — после смены настроек.</summary>
    public void ApplyVolume()
    {
        var gain = 1.0;
        if (_settings.NormalizeVolume && Stream?.LoudnessDb is { } loudness && loudness > 0)
        {
            // loudnessDb — насколько трек громче эталона YouTube: громкие приглушаются, тихие не усиливаются
            gain = Math.Pow(10, -loudness / 20);
        }
        _player.Volume = Math.Clamp(_settings.Volume * gain, 0, 1);
        _player.IsMuted = _settings.Muted;
        _player.PlaybackSession.PlaybackRate = Math.Clamp(_settings.Speed, 0.5, 2);
    }

    /// <summary>Восстановить очередь после перезапуска: последний трек на паузе, без автостарта.</summary>
    public void Restore(QueueSnapshot snapshot)
    {
        Queue.Restore(snapshot);
        if (Current is null) return;
        _playWhenReady = false;
        _pendingStartMs = snapshot.PositionMs;
        SetStatus(PlayerStatus.Paused);
        TrackChanged?.Invoke();
    }

    private long _pendingStartMs;

    // ---------- Загрузка трека ----------

    private async Task LoadCurrentAsync(bool play, long startMs = 0)
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        var track = Current;
        if (track is null) return;

        if (startMs == 0 && _pendingStartMs > 0) startMs = _pendingStartMs;
        _pendingStartMs = 0;
        _playWhenReady = play;
        Error = null;
        _player.Pause();
        _player.Source = null;
        var old = _stream;
        _stream = null;
        old?.Dispose();
        SetStatus(PlayerStatus.Resolving);
        TrackChanged?.Invoke();
        UpdateSmtcPlaceholder(track);

        var attempt = 0;
        while (true)
        {
            try
            {
                var stream = await TakePreloaded(track.VideoId) ?? await OpenSourceAsync(track.VideoId, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    stream.Dispose();
                    return;
                }
                _stream = stream;
                stream.Failed += error => Post(() =>
                {
                    if (ReferenceEquals(_stream, stream)) SkipAfterError(new PlayerError(error is StreamException s ? s.Kind : StreamErrorKind.Network, error.Message, track));
                });
                var source = MediaSource.CreateFromMediaStreamSource(stream.CreateMediaStreamSource());
                var item = new MediaPlaybackItem(source);
                ApplyDisplayProperties(item, track);
                _player.Source = item;
                ApplyVolume();
                if (startMs > 0) _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(startMs);
                _skipsInRow = 0;
                if (_playWhenReady)
                {
                    _player.Play();
                    SetStatus(PlayerStatus.Buffering);
                }
                else SetStatus(PlayerStatus.Paused);
                _playedThisSession.Add(track.VideoId);
                PrefetchUpcoming();
                _ = MaybeLoadAutoplayAsync();
                return;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (StreamException e)
            {
                if (attempt < e.Retries)
                {
                    attempt++;
                    await Task.Delay(attempt == 1 ? 1000 : 3000);
                    if (cts.IsCancellationRequested) return;
                    continue;
                }
                SkipAfterError(new PlayerError(e.Kind, e.Message, track));
                return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
            {
                if (attempt < 2)
                {
                    attempt++;
                    await Task.Delay(attempt == 1 ? 1000 : 3000);
                    if (cts.IsCancellationRequested) return;
                    continue;
                }
                SkipAfterError(new PlayerError(StreamErrorKind.Network, e.Message, track));
                return;
            }
        }
    }

    /// <summary>Пропуск включён всегда; после трёх пропусков подряд — остановка с причиной (REWRITE §3.10.9).</summary>
    private void SkipAfterError(PlayerError error)
    {
        Error = error;
        _skipsInRow++;
        if (_skipsInRow >= 3 || Queue.PeekNext(true) is null)
        {
            _playWhenReady = false;
            SetStatus(PlayerStatus.Error);
            StateChanged?.Invoke();
            return;
        }
        Skipped?.Invoke(error);
        Queue.MoveNext(true);
        _ = LoadCurrentAsync(play: true);
    }

    private void OnFailed(MediaPlayerFailedEventArgs e)
    {
        if (Current is not { } track) return;
        // Поток уже прочитал и повторил сам; сюда доходит то, что не лечится повтором — пробуем свежий адрес один раз
        _resolver.Invalidate(track.VideoId);
        var position = (long)Position.TotalMilliseconds;
        if (Error is null && _stream is not null)
        {
            Error = new PlayerError(StreamErrorKind.Network, e.ErrorMessage, track);
            _ = LoadCurrentAsync(play: _playWhenReady, position);
            return;
        }
        SkipAfterError(new PlayerError(StreamErrorKind.Extractor, e.ErrorMessage ?? e.Error.ToString(), track));
    }

    private void OnEnded()
    {
        if (SleepAtTrackEnd)
        {
            // «До конца трека»: следующий трек встаёт на паузу в начале
            FinishListening();
            CancelSleepTimer();
            if (Queue.MoveNext(userAction: false)) _ = LoadCurrentAsync(play: false);
            else Pause();
            SleepTimerFired?.Invoke();
            return;
        }
        FinishListening();
        Next(userAction: false);
    }

    // ---------- Таймер сна ----------

    private Timer? _sleepTimer;

    /// <summary>Когда сработает таймер сна; null — не заведён или «до конца трека».</summary>
    public DateTimeOffset? SleepAt { get; private set; }

    public bool SleepAtTrackEnd { get; private set; }

    public bool SleepTimerSet => SleepAt is not null || SleepAtTrackEnd;

    public event Action? SleepTimerChanged;

    /// <summary>Таймер сработал: воспроизведение на паузе.</summary>
    public event Action? SleepTimerFired;

    /// <summary>Таймер сна: 15, 30, 45 или 60 минут (docs/PROMPT.md §4).</summary>
    public void SetSleepTimer(TimeSpan duration)
    {
        StopSleepTimer();
        SleepAt = DateTimeOffset.Now + duration;
        _sleepTimer = new Timer(_ => Post(() =>
        {
            CancelSleepTimer();
            Pause();
            SleepTimerFired?.Invoke();
        }), null, duration, Timeout.InfiniteTimeSpan);
        SleepTimerChanged?.Invoke();
    }

    public void SetSleepAtTrackEnd()
    {
        StopSleepTimer();
        SleepAtTrackEnd = true;
        SleepTimerChanged?.Invoke();
    }

    public void CancelSleepTimer()
    {
        StopSleepTimer();
        SleepTimerChanged?.Invoke();
    }

    private void StopSleepTimer()
    {
        _sleepTimer?.Dispose();
        _sleepTimer = null;
        SleepAt = null;
        SleepAtTrackEnd = false;
    }

    private void OnSessionStateChanged()
    {
        var state = _player.PlaybackSession.PlaybackState;
        if (_player.Source is null) return;
        SetStatus(state switch
        {
            MediaPlaybackState.Playing => PlayerStatus.Playing,
            MediaPlaybackState.Buffering or MediaPlaybackState.Opening => _playWhenReady ? PlayerStatus.Buffering : PlayerStatus.Paused,
            MediaPlaybackState.Paused => PlayerStatus.Paused,
            _ => Status,
        });
        if (state == MediaPlaybackState.Playing) Error = null;
    }

    // ---------- SMTC ----------

    private static void ApplyDisplayProperties(MediaPlaybackItem item, Track track)
    {
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = track.Title;
        props.MusicProperties.Artist = track.ArtistsText ?? "";
        props.MusicProperties.AlbumTitle = track.AlbumTitle ?? "";
        if (Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 544) is { } art)
            props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(art));
        item.ApplyDisplayProperties(props);
    }

    private void UpdateSmtcPlaceholder(Track track)
    {
        var smtc = _player.SystemMediaTransportControls;
        smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        smtc.DisplayUpdater.MusicProperties.Title = track.Title;
        smtc.DisplayUpdater.MusicProperties.Artist = track.ArtistsText ?? "";
        smtc.DisplayUpdater.Update();
    }

    // ---------- Упреждающий резолв и автовоспроизведение ----------

    /// <summary>Кэш песен (null — без него): прочитанное ложится на диск, целиком прочитанный трек играет без сети.</summary>
    public SongCache? Songs { get; set; }

    private Task<AacStreamSource> OpenSourceAsync(string videoId, CancellationToken ct) => Task.Run(async () =>
    {
        // Трек целиком в кэше играет без запросов: ни player, ни адреса
        var info = Songs?.Complete(videoId) ?? await _resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);
        var cache = Songs?.Entry(info);
        try
        {
            return await AacStreamSource.OpenAsync(_http, info, async token =>
            {
                _resolver.Invalidate(videoId);
                return await _resolver.ResolveAsync(videoId, token).ConfigureAwait(false);
            }, ct, cache).ConfigureAwait(false);
        }
        catch
        {
            cache?.Release();
            throw;
        }
    }, ct);

    /// <summary>Заранее открытый источник трека, если он готов и не сломан; иначе null.</summary>
    private async Task<AacStreamSource?> TakePreloaded(string videoId)
    {
        if (!_preloaded.Remove(videoId, out var task)) return null;
        try
        {
            var source = await task;
            if (source.Info.ExpiresAtMs > IsoTime.NowMs()) return source;
            // Адрес истёк: источник не нужен — закрыть, чтобы кэш не держал трек закреплённым
            source.Dispose();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Адреса двух следующих треков — заранее, а у ближайшего ещё и начало звука (docs/PROMPT.md §4): переход по очереди
    /// не ждёт сети.
    /// </summary>
    private void PrefetchUpcoming()
    {
        var upcoming = Queue.Upcoming(2).Select(i => Queue.Items[i].Track.VideoId).ToList();
        foreach (var stale in _preloaded.Keys.Where(k => !upcoming.Contains(k)).ToList())
        {
            if (_preloaded.Remove(stale, out var old)) _ = old.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        if (upcoming.Count > 0 && !_preloaded.ContainsKey(upcoming[0]))
        {
            var task = OpenSourceAsync(upcoming[0], CancellationToken.None);
            _ = task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            _preloaded[upcoming[0]] = task;
        }
        foreach (var index in Queue.Upcoming(2))
        {
            var videoId = Queue.Items[index].Track.VideoId;
            // Целиком в кэше — адрес не нужен
            if (Songs?.IsComplete(videoId) == true) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _resolver.ResolveAsync(videoId).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ошибка всплывёт при переходе на трек — там её и обработаем
                }
            });
        }
    }

    private void ResetAutoplay()
    {
        _autoplayContinuation = null;
        _autoplayPlaylistId = null;
        _autoplaySeed = null;
        _autoplaySeedsWithoutNew = 0;
    }

    /// <summary>
    /// Автовоспроизведение похожих (REWRITE §4.10.5): догружается, когда впереди ≤ 3 треков автоплея; без повторов
    /// (очередь, прослушанное за сессию, «Не показывать»); по продолжению, а без него — от последнего трека автоплея.
    /// </summary>
    private async Task MaybeLoadAutoplayAsync()
    {
        if (_autoplayLoading || !_settings.Autoplay || Queue.Repeat == RepeatMode.All || Current is null) return;
        if (Queue.AutoplayAhead > 3) return;
        // Список, который пользователь запустил сам, играет до конца: похожие — только после него
        var ahead = Queue.Upcoming(int.MaxValue).Count;
        if (ahead > 3) return;
        _autoplayLoading = true;
        try
        {
            var hidden = _library.HiddenTracks();
            var known = Queue.Items.Select(i => i.Track.VideoId).ToHashSet();
            NextPage page;
            if (_autoplayContinuation is not null)
            {
                page = await _music.NextContinuationAsync(_autoplayContinuation, _autoplayPlaylistId);
            }
            else
            {
                var seed = Queue.Items.LastOrDefault(i => i.FromAutoplay && i.Track.VideoId != _autoplaySeed)?.Track
                           ?? Queue.Items.Take(Queue.AutoplayStart).LastOrDefault()?.Track ?? Current;
                if (seed is null) return;
                _autoplaySeed = seed.VideoId;
                page = await _music.NextAsync(seed.VideoId, "RDAMVM" + seed.VideoId);
            }
            var fresh = page.Tracks
                .Where(t => !t.Unavailable && !known.Contains(t.VideoId) && !_playedThisSession.Contains(t.VideoId) && !hidden.Contains(t.VideoId))
                .DistinctBy(t => t.VideoId)
                .Take(25)
                .ToList();
            _autoplayContinuation = page.Continuation == _autoplayContinuation ? null : page.Continuation;
            _autoplayPlaylistId = page.PlaylistId ?? _autoplayPlaylistId;
            if (fresh.Count == 0)
            {
                _autoplayContinuation = null;
                if (++_autoplaySeedsWithoutNew >= 3) return;
            }
            else _autoplaySeedsWithoutNew = 0;
            Queue.AppendAutoplay(fresh);
            PrefetchUpcoming();
        }
        catch (YouTubeException)
        {
            // Нет сети — попробуем при следующем переходе
        }
        finally
        {
            _autoplayLoading = false;
        }
    }

    // ---------- Учёт прослушиваний (DESIGN §3.11.1) ----------

    private void UpdateListening()
    {
        if (Status == PlayerStatus.Playing)
        {
            if (!ReferenceEquals(_listenedTrack, Current))
            {
                FinishListening();
                _listenedTrack = Current;
            }
            _listened.Start();
        }
        else _listened.Stop();
    }

    /// <summary>Сеанс элемента закончился (переход, остановка): ≥ 5 с реального звучания — одно прослушивание.</summary>
    private void FinishListening()
    {
        _listened.Stop();
        var track = _listenedTrack;
        var ms = (long)(_listened.Elapsed.TotalMilliseconds * Math.Clamp(_settings.Speed, 0.5, 2));
        _listened.Reset();
        _listenedTrack = null;
        if (track is null || ms < MinPlayMs || _settings.PauseHistory) return;
        var ended = IsoTime.NowMs();
        _ = Task.Run(() =>
        {
            try
            {
                _library.RecordPlay(track, ms, ended);
            }
            catch (Exception)
            {
                // История — не повод ронять плеер
            }
        });
    }

    /// <summary>Снимок для восстановления после перезапуска.</summary>
    public QueueSnapshot Snapshot() => Queue.Snapshot((long)Position.TotalMilliseconds);

    public void Dispose()
    {
        FinishListening();
        _sleepTimer?.Dispose();
        _load?.Cancel();
        foreach (var task in _preloaded.Values) _ = task.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        _player.Dispose();
        _stream?.Dispose();
        _http.Dispose();
    }
}
