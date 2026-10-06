using Melogold.Core.Domain;
using Melogold.Core.Music;

namespace Melogold.Server;

/// <summary>Что играет здесь — для <c>PUT /playback/state</c> (tasks/0017). Очередь — в порядке воспроизведения.</summary>
/// <param name="Volume">громкость плеера приложения 0..100</param>
public sealed record LocalPlayback(IReadOnlyList<Track> Queue, int Index, long PositionMs, long? DurationMs, bool Playing, int Volume);

/// <summary>Маршруты воспроизведения и пульта (API §4.9): отдельно от <see cref="AccountService"/>, чтобы проверять без сети.</summary>
public interface IPlaybackServer
{
    bool SignedIn { get; }

    Task<PlaybackPutResult> PutAsync(PlaybackPut put, CancellationToken ct);

    Task<PlaybackStateResponse> StateAsync(CancellationToken ct);

    Task<RemoteDeviceList> DevicesAsync(CancellationToken ct);

    Task<RemoteCommandResult> CommandAsync(RemoteCommand command, CancellationToken ct);
}

public sealed class AccountPlaybackServer(AccountService account) : IPlaybackServer
{
    public bool SignedIn => account.Session is not null;

    public Task<PlaybackPutResult> PutAsync(PlaybackPut put, CancellationToken ct) => account.PutPlaybackStateAsync(put, ct);

    public Task<PlaybackStateResponse> StateAsync(CancellationToken ct) => account.PlaybackStateAsync(ct);

    public Task<RemoteDeviceList> DevicesAsync(CancellationToken ct) => account.PlaybackDevicesAsync(ct);

    public Task<RemoteCommandResult> CommandAsync(RemoteCommand command, CancellationToken ct) => account.SendPlaybackCommandAsync(command, ct);
}

/// <summary>Часы сервера: <c>at</c> состояний — время сервера, часы устройства могут от него отставать.</summary>
public sealed class ServerClock(Func<long>? localNow = null)
{
    private readonly Func<long> _local = localNow ?? IsoTime.NowMs;
    private long _offset;

    public long LocalNow => _local();

    public long Now => _local() + Interlocked.Read(ref _offset);

    /// <summary>По <c>serverTime</c> ответа: задержка сети мала против секунд, которые считает пульт.</summary>
    public void Observe(string? serverTime)
    {
        if (IsoTime.TryParse(serverTime) is { } server) Interlocked.Exchange(ref _offset, server - _local());
    }
}

/// <summary>
/// Сообщает серверу, что играет здесь (API §4.9, tasks/0017): при смене трека, паузе, воспроизведении, перемотке, смене
/// очереди и громкости — не чаще раза в секунду. Очередь — до 200 треков вокруг текущего, <c>queueVersion</c> растёт при
/// каждой её смене, а сама очередь уходит, только когда сервер её ещё не видел. Позиция между отчётами не шлётся: другие
/// считают её от <c>at</c>. Пока в этом запуске ничего не включали, не сообщает: восстановленная на паузе очередь не
/// должна перебивать то, что играет на телефоне.
/// </summary>
public sealed class PlaybackReporter
{
    public const int MaxQueue = 200;
    private const long MinIntervalMs = 1000;
    private const long PositionSlackMs = 1500;
    private const long FirstBackoffMs = 2000;
    private const long MaxBackoffMs = 60_000;

    private readonly IPlaybackServer _server;
    private readonly Func<LocalPlayback?> _snapshot;
    private readonly ServerClock _clock;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<string, Exception?> _log;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly object _lock = new();
    private long _lastSentAt = long.MinValue / 2;
    private bool _scheduled;
    private bool _active;
    private int _queueVersion;
    private string? _queueSignature;
    private int _knownVersion = -1;
    private Sent? _last;
    private PlaybackHandoffInput? _handoff;

    /// <summary>После сбоя сервера следующий отчёт — не раньше: 2, 4, 8… до 60 с (как у Apple), а не каждую секунду.</summary>
    private long _retryAt;
    private long _backoffMs;

    public PlaybackReporter(IPlaybackServer server, Func<LocalPlayback?> snapshot, ServerClock clock, Action<string, Exception?>? log = null, Func<TimeSpan, Task>? delay = null)
    {
        _server = server;
        _snapshot = snapshot;
        _clock = clock;
        _log = log ?? ((_, _) => { });
        _delay = delay ?? (wait => Task.Delay(wait));
    }

    /// <summary>Сессия воспроизведения этого запуска (<c>sessionId</c>): по ней узнаётся «Слушать здесь» с этого устройства.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString();

    /// <summary>Воспроизведение забрали на другое устройство (<c>handed_off</c>): здесь — пауза.</summary>
    public event Action? HandedOff;

    private sealed record Sent(int QueueVersion, int Index, bool Playing, int Volume, long PositionMs, long? DurationMs, long At)
    {
        public bool Covers(Sent next) =>
            next.QueueVersion == QueueVersion && next.Index == Index && next.Playing == Playing && next.Volume == Volume && next.DurationMs == DurationMs
            && Math.Abs(next.PositionMs - (Playing ? PositionMs + (next.At - At) : PositionMs)) < PositionSlackMs;
    }

    /// <summary>Что-то поменялось: отчёт уйдёт сразу или через остаток секунды — с тем, что будет к тому времени.</summary>
    public void Changed()
    {
        lock (_lock)
        {
            if (_scheduled) return;
            _scheduled = true;
        }
        _ = SendSoonAsync();
    }

    /// <summary>«Слушать здесь»: следующий отчёт забирает воспроизведение у <paramref name="deviceId"/> (API §4.9).</summary>
    public void TakeOverFrom(string deviceId, string sessionId)
    {
        _handoff = new PlaybackHandoffInput(deviceId, sessionId);
        Changed();
    }

    private async Task SendSoonAsync()
    {
        var wait = Math.Max(_lastSentAt + MinIntervalMs, Interlocked.Read(ref _retryAt)) - _clock.LocalNow;
        if (wait > 0) await _delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
        lock (_lock) _scheduled = false;
        await SendAsync().ConfigureAwait(false);
    }

    /// <summary>Отчёт сейчас; тестам — без ожидания секунды.</summary>
    public async Task SendAsync(CancellationToken ct = default)
    {
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_server.SignedIn || _snapshot() is not { Queue.Count: > 0 } now) return;
            if (now.Playing) _active = true;
            if (!_active && _handoff is null) return;
            var (window, index) = Window(now.Queue, now.Index);
            var signature = string.Join(",", window.Select(t => t.VideoId));
            if (signature != _queueSignature)
            {
                _queueSignature = signature;
                _queueVersion++;
            }
            var at = _clock.Now;
            var sent = new Sent(_queueVersion, index, now.Playing, Math.Clamp(now.Volume, 0, 100), Math.Max(0, now.PositionMs), now.DurationMs, at);
            if (_handoff is null && _last is { } last && last.Covers(sent)) return;
            _lastSentAt = _clock.LocalNow;
            await PutAsync(window, sent, withQueue: _knownVersion != _queueVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task PutAsync(IReadOnlyList<Track> window, Sent sent, bool withQueue, CancellationToken ct)
    {
        var put = new PlaybackPut
        {
            SessionId = SessionId,
            QueueVersion = sent.QueueVersion,
            At = IsoTime.Format(sent.At),
            Index = sent.Index,
            PositionMs = sent.PositionMs,
            DurationMs = sent.DurationMs,
            Playing = sent.Playing,
            Volume = sent.Volume,
            Queue = withQueue ? window.Select(TrackDtos.Input).ToList() : null,
            HandoffFrom = _handoff,
        };
        try
        {
            var result = await _server.PutAsync(put, ct).ConfigureAwait(false);
            _clock.Observe(result.ServerTime);
            _backoffMs = 0;
            Interlocked.Exchange(ref _retryAt, 0);
            _last = sent;
            if (result.Applied)
            {
                _knownVersion = sent.QueueVersion;
                _handoff = null;
            }
            else if (result.Reason == "handed_off")
            {
                _active = false;
                HandedOff?.Invoke();
            }
        }
        catch (ApiException e) when (e.Code == "playback_queue_required" && !withQueue)
        {
            // Сервер не знает эту очередь (перезапустился, другой сервер): ещё раз, с ней
            _knownVersion = -1;
            await PutAsync(window, sent, withQueue: true, ct).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.IsTransient)
        {
            // Нет сети, сервер занят: то же состояние — ещё раз, с растущим отступом
            _backoffMs = Math.Clamp(_backoffMs * 2, FirstBackoffMs, MaxBackoffMs);
            Interlocked.Exchange(ref _retryAt, _clock.LocalNow + _backoffMs);
            _log($"Playback state not sent: {e.Code}; again in {_backoffMs / 1000} s", null);
            Changed();
        }
        catch (ApiException e)
        {
            _log("Playback state not sent: " + e.Code, e);
        }
    }

    /// <summary>До 200 треков вокруг текущего: четверть — позади, остальное — впереди.</summary>
    public static (IReadOnlyList<Track> Window, int Index) Window(IReadOnlyList<Track> queue, int index)
    {
        index = Math.Clamp(index, 0, queue.Count - 1);
        if (queue.Count <= MaxQueue) return (queue, index);
        var start = Math.Clamp(index - MaxQueue / 4, 0, queue.Count - MaxQueue);
        return (queue.Skip(start).Take(MaxQueue).ToList(), index - start);
    }

    /// <summary>
    /// Автопауза (API §6): воспроизведение забрали с этого устройства «Слушать здесь» — <c>handoffFrom</c> указывает на
    /// него и на эту сессию, и это было меньше 5 минут назад.
    /// </summary>
    public bool IsTakenFromHere(PlaybackSummary? state, string? deviceId) =>
        state?.HandoffFrom is { } handoff && deviceId is not null && handoff.DeviceId == deviceId && handoff.SessionId == SessionId
        && IsoTime.TryParse(handoff.At) is { } at && _clock.Now - at < TimeSpan.FromMinutes(5).TotalMilliseconds;
}

/// <summary>Почему пульт отключился.</summary>
public enum RemoteFailure
{
    /// <summary><c>device_offline</c>: «„MacBook Air“ не в сети».</summary>
    Offline,

    /// <summary><c>remote_control_disabled</c>: «На „MacBook Air“ управление выключено».</summary>
    Disabled,

    /// <summary>Нет сети у самого пульта.</summary>
    Network,

    Other,
}

/// <summary>
/// Пульт (API §4.9, tasks/0017): управлять воспроизведением другого своего устройства через сервер. Что там играет —
/// из <c>GET /playback/state</c> и <c>playback.updated</c>, позиция — от <c>at</c>; команды — <c>POST /playback/commands</c>,
/// до ответа цели кнопки показывают результат сразу.
/// </summary>
public sealed class RemoteController(IPlaybackServer server, ServerClock clock, Action<string, Exception?>? log = null)
{
    private readonly Action<string, Exception?> _log = log ?? ((_, _) => { });

    /// <summary>Какое устройство слушается; null — плеер управляет этим устройством.</summary>
    public RemoteDevice? Target { get; private set; }

    /// <summary>Что играет на <see cref="Target"/>; null — там ничего не играет.</summary>
    public PlaybackSummary? State { get; private set; }

    public event Action? Changed;

    /// <summary>Пульт отключился сам: цель ушла из сети или запретила управление.</summary>
    public event Action<RemoteFailure, string>? Failed;

    /// <summary>
    /// Устройства, на которые можно включить музыку. Часов в списке нет (tasks/0025, доктрина §4.8): звук на часах играет
    /// только приложение, открытое на самих часах, — включать его удалённо бессмысленно.
    /// </summary>
    public async Task<IReadOnlyList<RemoteDevice>> DevicesAsync(CancellationToken ct = default)
    {
        var list = await server.DevicesAsync(ct).ConfigureAwait(false);
        clock.Observe(list.ServerTime);
        return list.Devices.Where(CanPlay).ToList();
    }

    /// <summary>Может ли устройство играть по команде пульта: всё, кроме часов.</summary>
    public static bool CanPlay(RemoteDevice device) => !string.Equals(device.Platform, "watchos", StringComparison.OrdinalIgnoreCase);

    public async Task ConnectAsync(RemoteDevice device, CancellationToken ct = default)
    {
        Target = device;
        State = device.Playing;
        Changed?.Invoke();
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        if (Target is null) return;
        Target = null;
        State = null;
        Changed?.Invoke();
    }

    /// <summary>Перечитать состояние цели (пульт открыт, поток событий переподключился).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Target is not { } target) return;
        try
        {
            var response = await server.StateAsync(ct).ConfigureAwait(false);
            clock.Observe(response.ServerTime);
            if (Target?.DeviceId != target.DeviceId) return;
            State = response.State is { } state && state.DeviceId == target.DeviceId ? Summary(state) : null;
            Changed?.Invoke();
        }
        catch (ApiException e)
        {
            _log("Remote state not loaded: " + e.Code, null);
        }
    }

    /// <summary><c>playback.updated</c>: состояние цели — новое; очищенное или чужое — «там ничего не играет».</summary>
    public void OnUpdated(PlaybackUpdatedPayload update)
    {
        if (Target is not { } target) return;
        if (update.Cleared) State = null;
        else if (update.State is { } state && state.DeviceId == target.DeviceId) State = state;
        else if (update.State is not null) State = null;
        else return;
        Changed?.Invoke();
    }

    /// <summary>Позиция на цели сейчас: у играющего — от <c>at</c>, не дальше длительности.</summary>
    public long Position => State is { } state ? PositionAt(state, clock.Now) : 0;

    public static long PositionAt(PlaybackSummary state, long serverNow)
    {
        var position = state.PositionMs;
        if (state.Playing && IsoTime.TryParse(state.At) is { } at) position += Math.Max(0, serverNow - at);
        return DurationOf(state) is { } duration ? Math.Min(position, duration) : position;
    }

    /// <summary>
    /// Длительность трека на цели: из состояния, а если устройство её не сообщило (<c>durationMs</c> пуст или 0) — из
    /// самого трека (<c>durationMs</c>, затем «3:45»); null — неизвестна, ползунок не показывает конец.
    /// </summary>
    public static long? DurationOf(PlaybackSummary? state)
    {
        if (state is null) return null;
        if (state.DurationMs is > 0 and var reported) return reported;
        if (state.Track?.DurationMs is > 0 and var known) return known;
        return Durations.ParseText(state.Track?.DurationText) is > 0 and var parsed ? parsed : null;
    }

    public Task<bool> PlayAsync() => SendAsync("play");

    public Task<bool> PauseAsync() => SendAsync("pause");

    public Task<bool> ToggleAsync() => SendAsync("toggle");

    public Task<bool> NextAsync() => SendAsync("next");

    public Task<bool> PreviousAsync() => SendAsync("previous");

    public Task<bool> SeekAsync(long positionMs) => SendAsync("seek", positionMs: Math.Max(0, positionMs));

    public Task<bool> SetVolumeAsync(int volume) => SendAsync("volume", volume: Math.Clamp(volume, 0, 100));

    /// <summary>Нажатие по треку в списке, пока пульт включён: этот список на цели с этого трека.</summary>
    public Task<bool> PlayQueueAsync(IReadOnlyList<Track> tracks, int index)
    {
        if (tracks.Count == 0) return Task.FromResult(false);
        var (window, start) = PlaybackReporter.Window(tracks, index);
        return SendAsync("play_queue", queue: window, index: start);
    }

    public async Task<bool> SendAsync(string action, long? positionMs = null, int? volume = null, IReadOnlyList<Track>? queue = null, int? index = null, CancellationToken ct = default)
    {
        if (Target is not { } target) return false;
        var command = new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TargetDeviceId = target.DeviceId,
            Action = action,
            PositionMs = positionMs,
            Volume = volume,
            Queue = queue?.Select(TrackDtos.Input).ToList(),
            Index = index,
        };
        Anticipate(action, positionMs, volume);
        try
        {
            var result = await server.CommandAsync(command, ct).ConfigureAwait(false);
            if (result.Delivered) return true;
            Fail(RemoteFailure.Offline, target);
        }
        catch (ApiException e) when (e.Code == "device_offline")
        {
            Fail(RemoteFailure.Offline, target);
        }
        catch (ApiException e) when (e.Code == "remote_control_disabled")
        {
            Fail(RemoteFailure.Disabled, target);
        }
        catch (ApiException e) when (e.IsNetwork)
        {
            Failed?.Invoke(RemoteFailure.Network, target.Name);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (ApiException e)
        {
            _log("Remote command failed: " + e.Code, e);
            Failed?.Invoke(RemoteFailure.Other, target.Name);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        return false;
    }

    private void Fail(RemoteFailure failure, RemoteDevice target)
    {
        Disconnect();
        Failed?.Invoke(failure, target.Name);
    }

    /// <summary>Кнопка показывает итог сразу; настоящее состояние придёт от цели (<c>playback.updated</c>).</summary>
    private void Anticipate(string action, long? positionMs, int? volume)
    {
        if (State is not { } state) return;
        var now = IsoTime.Format(clock.Now);
        var position = PositionAt(state, clock.Now);
        State = action switch
        {
            "play" => state with { Playing = true, PositionMs = position, At = now },
            "pause" or "stop" => state with { Playing = false, PositionMs = position, At = now },
            "toggle" => state with { Playing = !state.Playing, PositionMs = position, At = now },
            "seek" when positionMs is { } seek => state with { PositionMs = seek, At = now },
            "volume" when volume is { } level => state with { Volume = level },
            _ => state,
        };
        if (!ReferenceEquals(State, state)) Changed?.Invoke();
    }

    /// <summary>«Слушать здесь»: полное состояние цели с очередью — играть его здесь с того же места.</summary>
    public async Task<PlaybackState?> TakeOverAsync(CancellationToken ct = default)
    {
        if (Target is not { } target) return null;
        var response = await server.StateAsync(ct).ConfigureAwait(false);
        clock.Observe(response.ServerTime);
        return response.State is { } state && state.DeviceId == target.DeviceId ? state : null;
    }

    public static PlaybackSummary Summary(PlaybackState state) => new(
        state.Rev, state.DeviceId, state.DeviceName, state.SessionId, state.QueueVersion, state.Index, state.Queue.Count,
        state.Index >= 0 && state.Index < state.Queue.Count ? state.Queue[state.Index] : null,
        state.PositionMs, state.DurationMs, state.Playing, state.At, state.UpdatedAt, state.HandoffFrom, state.Volume);
}

/// <summary>Плеер этого устройства для команд пульта.</summary>
public interface IRemotePlayer
{
    void Play();

    void Pause();

    void Toggle();

    void Next();

    void Previous();

    void Seek(long positionMs);

    /// <summary>Громкость плеера приложения 0..100.</summary>
    void SetVolume(int volume);

    void PlayQueue(IReadOnlyList<Track> tracks, int index);

    void Stop();
}

/// <summary>
/// Выполняет команды пульта (<c>playback.command</c>, tasks/0017) своим плеером; итог уходит обычным отчётом. Раз в 30 с
/// не чаще — короткое «Управляет „Pixel 7 Pro“».
/// </summary>
public sealed class RemoteCommandHandler(IRemotePlayer player, Action<string> announce, Func<long>? now = null)
{
    private const long AnnounceEveryMs = 30_000;
    private readonly Func<long> _now = now ?? IsoTime.NowMs;
    private long _announcedAt = long.MinValue / 2;

    /// <summary>false — действие незнакомое или без своих полей: ничего не сделано.</summary>
    public bool Execute(PlaybackCommandPayload command)
    {
        switch (command.Action)
        {
            case "play":
                player.Play();
                break;
            case "pause":
                player.Pause();
                break;
            case "toggle":
                player.Toggle();
                break;
            case "next":
                player.Next();
                break;
            case "previous":
                player.Previous();
                break;
            case "seek" when command.PositionMs is { } position:
                player.Seek(Math.Max(0, position));
                break;
            case "volume" when command.Volume is { } volume:
                player.SetVolume(Math.Clamp(volume, 0, 100));
                break;
            case "play_queue" when command.Queue is { Count: > 0 } queue && command.Index is { } index:
                player.PlayQueue(queue.Select(TrackDtos.ToTrackOrStub).ToList(), Math.Clamp(index, 0, queue.Count - 1));
                break;
            case "stop":
                player.Stop();
                break;
            default:
                return false;
        }
        var time = _now();
        if (time - _announcedAt >= AnnounceEveryMs)
        {
            _announcedAt = time;
            announce(command.FromDeviceName ?? "");
        }
        return true;
    }
}
