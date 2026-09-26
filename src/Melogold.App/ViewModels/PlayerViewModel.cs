using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.UI.Dispatching;

namespace Melogold.App.ViewModels;

/// <summary>
/// Состояние панели воспроизведения (§5.2): трек, кнопки, ползунок, громкость, «Получаем поток…» через 3 с ожидания,
/// причина ошибки и «Повторить». Сохраняет очередь и позицию, чтобы после перезапуска вернуть их без автостарта.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Library _library;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueueTimer _timer;
    private DateTime _resolvingSince;
    private DateTime _lastSave;
    private bool _seeking;
    private bool _initializing = true;

    public PlayerViewModel(PlayerEngine engine, Library library, SettingsStore settings)
    {
        Engine = engine;
        _library = library;
        _settings = settings;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        engine.StateChanged += Refresh;
        engine.TrackChanged += () =>
        {
            Refresh();
            SaveQueue();
        };
        engine.QueueChanged += SaveQueue;
        engine.Skipped += error => Notice = Loc.Format("SkippedFormat", error.Track.Title, ErrorText(error));
        library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Likes)) DispatcherQueue.GetForCurrentThread()?.TryEnqueue(RefreshLike);
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsStore.Volume) or nameof(SettingsStore.Muted) or nameof(SettingsStore.Speed) or nameof(SettingsStore.NormalizeVolume))
                engine.ApplyVolume();
        };
        Volume = _settings.Volume * 100;
        Repeat = (RepeatMode)_settings.Repeat;
        engine.Queue.SetRepeat(Repeat);
        RestoreQueue();
        Refresh();
        _initializing = false;
    }

    public PlayerEngine Engine { get; }

    [ObservableProperty]
    public partial bool HasTrack { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "";

    [ObservableProperty]
    public partial string? ArtworkUrl { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsResolving { get; set; }

    /// <summary>Подпись «Получаем поток…» — только если ждём дольше 3 с (§5.2).</summary>
    [ObservableProperty]
    public partial bool ShowResolvingText { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Разовое сообщение для плашки над плеером (пропуск трека).</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial double Duration { get; set; } = 1;

    [ObservableProperty]
    public partial string PositionText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0:00";

    [ObservableProperty]
    public partial bool IsLiked { get; set; }

    [ObservableProperty]
    public partial bool Shuffle { get; set; }

    [ObservableProperty]
    public partial RepeatMode Repeat { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; }

    public Track? Track => Engine.Current;

    /// <summary>Строка исполнителя уступает место «Получаем поток…» и причине ошибки.</summary>
    public bool ShowSubtitle => !ShowResolvingText && ErrorMessage is null;

    partial void OnShowResolvingTextChanged(bool value) => OnPropertyChanged(nameof(ShowSubtitle));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowSubtitle));

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";

    public string VolumeGlyph => _settings.Muted || Volume == 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";

    /// <summary>Громкость числом рядом с ползунком в панели громкости.</summary>
    public string VolumeText => Math.Round(Volume).ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string PlayPauseLabel => Loc.Get(IsPlaying ? "Pause" : "Play");

    public string RepeatLabel => Loc.Get(Repeat switch { RepeatMode.All => "RepeatAll", RepeatMode.One => "RepeatOne", _ => "RepeatOff" });

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseLabel));
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatLabel));
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeGlyph));
        OnPropertyChanged(nameof(VolumeText));
        // Начальное значение и повторная запись того же значения ползунком — не действие человека: «Без звука» не снимаем
        var volume = Math.Clamp(value / 100, 0, 1);
        if (_initializing || Math.Abs(volume - _settings.Volume) < 0.005) return;
        _settings.Volume = volume;
        if (value > 0 && _settings.Muted) _settings.Muted = false;
    }

    private void Refresh()
    {
        var track = Engine.Current;
        HasTrack = track is not null;
        Title = track?.Title ?? "";
        Subtitle = track?.Subtitle ?? "";
        ArtworkUrl = track is null ? null : Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 112);
        IsPlaying = Engine.IsPlaying;
        var resolving = Engine.Status is PlayerStatus.Resolving or PlayerStatus.Buffering && Engine.IsPlaying;
        if (resolving && !IsResolving) _resolvingSince = DateTime.UtcNow;
        IsResolving = resolving;
        ShowResolvingText = resolving && DateTime.UtcNow - _resolvingSince > TimeSpan.FromSeconds(3);
        ErrorMessage = Engine.Status == PlayerStatus.Error && Engine.Error is { } error ? ErrorText(error) : null;
        Shuffle = Engine.Queue.Shuffled;
        OnPropertyChanged(nameof(Track));
        RefreshLike();
        Tick();
    }

    private void RefreshLike() => IsLiked = Engine.Current is { } track && _library.IsLiked(track.VideoId);

    private void Tick()
    {
        if (IsResolving) ShowResolvingText = DateTime.UtcNow - _resolvingSince > TimeSpan.FromSeconds(3);
        var duration = Engine.Duration;
        Duration = Math.Max(1, duration.TotalSeconds);
        DurationText = Durations.Format(duration);
        if (!_seeking)
        {
            var position = Engine.Position;
            Position = Math.Min(position.TotalSeconds, Duration);
            PositionText = Durations.Format(position);
        }
        if (IsPlaying && DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(10)) SaveQueue();
    }

    /// <summary>
    /// Текст причины: трек закрыт в стране — со страной, где YouTube видит устройство, и числом стран, где трек открыт
    /// (задание 0010); иначе по классу ошибки.
    /// </summary>
    public static string ErrorText(PlayerError error)
    {
        if (error.Kind != StreamErrorKind.Geo || error.Country is not { } code) return ErrorText(error.Kind);
        var country = Melogold.Core.Domain.CountryNames.Of(code);
        return error.OpenCountries is { } open
            ? Loc.Format("PlayErrorGeoCountryOpenFormat", country, Loc.Plural("GeoOtherCountries", open))
            : Loc.Format("PlayErrorGeoCountryFormat", country);
    }

    /// <summary>Текст причины по классу ошибки (REWRITE §3.10.9, глоссарий §2.9).</summary>
    public static string ErrorText(StreamErrorKind kind) => Loc.Get(kind switch
    {
        StreamErrorKind.Network or StreamErrorKind.Timeout => "PlayErrorNetwork",
        StreamErrorKind.BotCheck => "PlayErrorBot",
        StreamErrorKind.Geo => "PlayErrorGeo",
        StreamErrorKind.Unavailable => "PlayErrorUnavailable",
        StreamErrorKind.Age => "PlayErrorAge",
        _ => "PlayErrorExtractor",
    });

    // ---------- Команды ----------

    [RelayCommand]
    private void PlayPause() => Engine.TogglePlayPause();

    [RelayCommand]
    private void Next() => Engine.Next();

    [RelayCommand]
    private void Previous() => Engine.Previous();

    [RelayCommand]
    private void Retry() => Engine.Retry();

    [RelayCommand]
    private void ToggleShuffle()
    {
        Engine.SetShuffle(!Engine.Queue.Shuffled);
        Shuffle = Engine.Queue.Shuffled;
        _settings.Shuffle = Shuffle;
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        Engine.SetRepeat(Repeat);
        _settings.Repeat = (int)Repeat;
    }

    [RelayCommand]
    private void ToggleLike()
    {
        if (Engine.Current is not { } track) return;
        _library.SetLiked(track, !IsLiked);
        RefreshLike();
    }

    [RelayCommand]
    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    /// <summary>Ползунок: во время перетаскивания позиция не прыгает от таймера.</summary>
    public void BeginSeek() => _seeking = true;

    public void EndSeek(double seconds)
    {
        _seeking = false;
        Engine.Seek(TimeSpan.FromSeconds(seconds));
        Position = seconds;
        PositionText = Durations.Format(TimeSpan.FromSeconds(seconds));
    }

    public void PreviewSeek(double seconds) => PositionText = Durations.Format(TimeSpan.FromSeconds(seconds));

    // ---------- Очередь после перезапуска ----------

    private sealed record SavedQueue(List<SavedItem> Items, int Index, List<int>? Shuffle, long PositionMs);

    private sealed record SavedItem(Track Track, bool FromAutoplay, long Id);

    public void SaveQueue()
    {
        _lastSave = DateTime.UtcNow;
        var snapshot = Engine.Snapshot();
        var saved = new SavedQueue(snapshot.Items.Select(i => new SavedItem(i.Track, i.FromAutoplay, i.Id)).ToList(), snapshot.Index,
            snapshot.ShuffleOrder?.ToList(), snapshot.PositionMs);
        var json = JsonSerializer.Serialize(saved, Json);
        _ = Task.Run(() =>
        {
            try
            {
                _library.SetState("queue", json);
            }
            catch (Exception e)
            {
                Log.Warn("Queue not saved", e);
            }
        });
    }

    private void RestoreQueue()
    {
        try
        {
            if (_library.GetState("queue") is not { } json) return;
            var saved = JsonSerializer.Deserialize<SavedQueue>(json, Json);
            if (saved is null || saved.Items.Count == 0) return;
            Engine.Restore(new QueueSnapshot(saved.Items.Select(i => new QueueItem(i.Track, i.FromAutoplay, i.Id)).ToList(), saved.Index, saved.Shuffle, saved.PositionMs));
        }
        catch (Exception e)
        {
            // Очередь — удобство: если её не прочитать, приложение всё равно открывается
            Log.Warn("Queue not restored", e);
        }
    }
}
