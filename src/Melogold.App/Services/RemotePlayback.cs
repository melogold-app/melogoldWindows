using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Melogold.Server;
using Microsoft.UI.Dispatching;

namespace Melogold.App.Services;

/// <summary>
/// Пульт и отчёт о воспроизведении в приложении (tasks/0017): что играет в <see cref="PlayerEngine"/> — серверу через
/// <see cref="PlaybackReporter"/>; команды пульта с других устройств (<c>playback.command</c>) — в тот же плеер (SMTC
/// видит изменения сам); «Слушать здесь» с другого устройства — пауза здесь. Плеер трогается только в потоке интерфейса.
/// </summary>
public sealed class RemotePlayback : IRemotePlayer
{
    private readonly PlayerEngine _engine;
    private readonly SettingsStore _settings;
    private readonly AccountService _account;
    private readonly Snackbar _snackbar;
    private readonly DispatcherQueue _ui;
    private readonly RemoteCommandHandler _commands;
    private readonly ServerClock _clock;
    private LocalPlayback? _latest;
    private long _capturedAt;

    public RemotePlayback(PlayerEngine engine, SettingsStore settings, AccountService account, LibrarySync sync, Snackbar snackbar,
        RemoteController remote, IPlaybackServer server, ServerClock clock)
    {
        _engine = engine;
        _settings = settings;
        _account = account;
        _snackbar = snackbar;
        _clock = clock;
        Remote = remote;
        _ui = DispatcherQueue.GetForCurrentThread();
        Reporter = new PlaybackReporter(server, Snapshot, clock, (message, error) => Log.Warn(message, error));
        _commands = new RemoteCommandHandler(this, name => _ui.TryEnqueue(() => _snackbar.Show(Loc.Format("RemoteControlledByFormat", name))));

        engine.StateChanged += Report;
        engine.TrackChanged += Report;
        engine.QueueChanged += Report;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsStore.Volume) or nameof(SettingsStore.Muted)) Report();
            if (e.PropertyName is nameof(SettingsStore.RemoteControl)) sync.RemoteAllowed = settings.RemoteControl;
        };
        sync.RemoteAllowed = settings.RemoteControl;
        Reporter.HandedOff += () => _ui.TryEnqueue(() => _engine.Pause());
        sync.PlaybackCommand += command => _ui.TryEnqueue(() =>
        {
            if (!_settings.RemoteControl) return;
            Log.Info($"Remote command {command.Action} from {command.FromDeviceId}");
            _commands.Execute(command);
        });
        sync.PlaybackUpdated += update => _ui.TryEnqueue(() =>
        {
            // «Слушать здесь» на другом устройстве забрало воспроизведение отсюда — здесь пауза (API §6)
            if (Reporter.IsTakenFromHere(update.State, _account.Session?.DeviceId) && _engine.IsPlaying) _engine.Pause();
            Remote.OnUpdated(update);
        });
        sync.LiveConnected += () => _ui.TryEnqueue(() => _ = Remote.RefreshAsync());
        account.StateChanged += state =>
        {
            if (state is not AccountState.SignedIn) _ui.TryEnqueue(Remote.Disconnect);
        };
        Remote.Failed += (failure, name) => _ui.TryEnqueue(() => _snackbar.Show(failure switch
        {
            RemoteFailure.Offline => Loc.Format("RemoteOfflineFormat", name),
            RemoteFailure.Disabled => Loc.Format("RemoteDisabledFormat", name),
            RemoteFailure.Network => Loc.Get("ErrorOffline"),
            _ => Loc.Get("ErrorUnknown"),
        }));
    }

    public PlaybackReporter Reporter { get; }

    public RemoteController Remote { get; }

    /// <summary>Громкость плеера приложения 0..100: «без звука» — 0.</summary>
    public int LocalVolume => _settings.Muted ? 0 : (int)Math.Round(_settings.Volume * 100);

    /// <summary>Состояние снимается в потоке интерфейса; отчёт может уйти позже — позиция дописывается от времени снимка.</summary>
    private void Report()
    {
        var order = _engine.Queue.PlayOrder;
        var items = _engine.Queue.Ordered;
        var index = order.ToList().IndexOf(_engine.Queue.Current);
        _latest = items.Count == 0 || index < 0 ? null : new LocalPlayback(
            items.Select(i => i.Track).ToList(), index, (long)_engine.Position.TotalMilliseconds,
            _engine.Duration > TimeSpan.Zero ? (long)_engine.Duration.TotalMilliseconds : null, _engine.IsPlaying, LocalVolume);
        _capturedAt = IsoTime.NowMs();
        Reporter.Changed();
    }

    private LocalPlayback? Snapshot() =>
        _latest is { } latest && latest.Playing ? latest with { PositionMs = latest.PositionMs + Math.Max(0, IsoTime.NowMs() - _capturedAt) } : _latest;

    /// <summary>
    /// Выбрано другое устройство (tasks/0031). Здесь играет — очередь переезжает туда с той же секунды, как AirPlay, здесь
    /// пауза; здесь не играет — просто управлять им, как раньше. Раньше выбор всегда открывал пустой пульт, и песню на
    /// другом устройстве приходилось выбирать заново.
    /// </summary>
    public async Task ConnectAsync(RemoteDevice device)
    {
        var order = _engine.Queue.PlayOrder;
        var items = _engine.Queue.Ordered;
        var index = order.ToList().IndexOf(_engine.Queue.Current);
        var handoff = _engine.IsPlaying && items.Count > 0 && index >= 0
            ? (Tracks: items.Select(i => i.Track).ToList(), Index: index, PositionMs: (long)_engine.Position.TotalMilliseconds)
            : default;
        if (handoff.Tracks is not null) _engine.Pause();
        await Remote.ConnectAsync(device);
        if (handoff.Tracks is not null)
        {
            Log.Info($"Handoff to {device.Name}: {handoff.Tracks[handoff.Index].VideoId} at {handoff.PositionMs / 1000} s");
            await Remote.PlayQueueAsync(handoff.Tracks, handoff.Index, handoff.PositionMs);
        }
    }

    /// <summary>«Слушать здесь»: очередь цели с того же места — здесь, цель ставит паузу сама (<c>handoffFrom</c>).</summary>
    public async Task ListenHereAsync()
    {
        if (Remote.Target is not { } target) return;
        PlaybackState? state;
        try
        {
            state = await Remote.TakeOverAsync();
        }
        catch (ApiException e)
        {
            Log.Warn("Take over failed", e);
            _snackbar.Show(Loc.Get(e.IsNetwork ? "ErrorOffline" : "ErrorUnknown"));
            return;
        }
        Remote.Disconnect();
        if (state is null || state.Queue.Count == 0)
        {
            _snackbar.Show(Loc.Format("RemoteNothingFormat", target.Name));
            return;
        }
        var tracks = state.Queue.Select(TrackDtos.ToTrackOrStub).ToList();
        var position = RemoteController.PositionAt(RemoteController.Summary(state), _clock.Now);
        _engine.PlayList(tracks, Math.Clamp(state.Index, 0, tracks.Count - 1), startMs: position);
        Reporter.TakeOverFrom(target.DeviceId, state.SessionId);
    }

    // ---------- Команды пульта этому устройству ----------

    public void Play() => _engine.Play();

    public void Pause() => _engine.Pause();

    public void Toggle() => _engine.TogglePlayPause();

    public void Next() => _engine.Next();

    public void Previous() => _engine.Previous();

    public void Seek(long positionMs) => _engine.Seek(TimeSpan.FromMilliseconds(positionMs));

    public void SetVolume(int volume)
    {
        _settings.Volume = volume / 100.0;
        if (volume > 0) _settings.Muted = false;
    }

    public void PlayQueue(IReadOnlyList<Track> tracks, int index, long startMs = 0) => _engine.PlayList(tracks, index, startMs: startMs);

    public void Stop() => _engine.Pause();
}
