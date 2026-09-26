using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Melogold.Playback;

namespace Melogold.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Настройки устройства (JSON в папке данных). Только то, что есть на Android: каждая настройка множится на все
/// клиенты (docs/PROMPT.md §5.4). Запись — сразу при изменении, атомарной заменой файла.
/// </summary>
public sealed partial class SettingsStore : ObservableObject, IPlaybackSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private bool _loading;

    public SettingsStore(string path)
    {
        _path = path;
        Load();
        PropertyChanged += (_, _) => Save();
    }

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Раздел при выходе: приложение открывается в нём (docs/PROMPT.md §5.1).</summary>
    [ObservableProperty]
    public partial string LastSection { get; set; } = "trends";

    [ObservableProperty]
    public partial double Volume { get; set; } = 0.8;

    [ObservableProperty]
    public partial bool Muted { get; set; }

    /// <summary>Скорость 0,5–2× — одна глобальная настройка (docs/PROMPT.md §4).</summary>
    [ObservableProperty]
    public partial double Speed { get; set; } = 1.0;

    [ObservableProperty]
    public partial bool NormalizeVolume { get; set; } = true;

    /// <summary>Повтор: 0 — выкл, 1 — очередь, 2 — трек.</summary>
    [ObservableProperty]
    public partial int Repeat { get; set; }

    [ObservableProperty]
    public partial bool Shuffle { get; set; }

    /// <summary>Автовоспроизведение похожих в конце очереди.</summary>
    [ObservableProperty]
    public partial bool Autoplay { get; set; } = true;

    /// <summary>«Не сохранять историю» (DESIGN §3.11.2).</summary>
    [ObservableProperty]
    public partial bool PauseHistory { get; set; }

    [ObservableProperty]
    public partial string? ServerUrl { get; set; }

    [ObservableProperty]
    public partial long LastUpdateCheck { get; set; }

    /// <summary>Не сохранять новые поисковые запросы и не показывать историю поиска (Android <c>pause_search_history</c>).</summary>
    [ObservableProperty]
    public partial bool PauseSearchHistory { get; set; }

    /// <summary>Показывать синхронный текст, когда он есть (Android <c>PlayerPreferences</c>).</summary>
    [ObservableProperty]
    public partial bool PreferSyncedLyrics { get; set; } = true;


    /// <summary>«Максимальный размер» кэша изображений, МБ (Android <c>coilDiskCacheMaxSize</c>, 128 МБ).</summary>
    [ObservableProperty]
    public partial long ImageCacheMaxMb { get; set; } = 128;

    /// <summary>«Размер кэша» музыки, МБ; 0 — без ограничения (tasks/0003: по умолчанию 4 ГБ).</summary>
    [ObservableProperty]
    public partial long SongCacheMaxMb { get; set; } = 4096;

    /// <summary>Размер кэша выбрал человек: новое значение по умолчанию его не меняет.</summary>
    [ObservableProperty]
    public partial bool SongCacheSizeChosen { get; set; }

    /// <summary>Версия, о которой уже сказали окном или уведомлением: о каждой — один раз, дальше значок «!».</summary>
    [ObservableProperty]
    public partial string? UpdateAnnouncedVersion { get; set; }

    /// <summary>Место и размер главного окна при закрытии (<see cref="Services.WindowPlacement"/>).</summary>
    [ObservableProperty]
    public partial string? WindowPlacement { get; set; }

    /// <summary>Где стоял мини-плеер: «x,y» в пикселях экрана.</summary>
    [ObservableProperty]
    public partial string? MiniPlayerPosition { get; set; }

    /// <summary>Сортировки списков по ключу экрана.</summary>
    [ObservableProperty]
    public partial Dictionary<string, string> Sorts { get; set; } = [];

    private void Load()
    {
        _loading = true;
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_path), Json);
            if (data is null) return;
            Theme = data.Theme;
            LastSection = data.LastSection ?? "trends";
            Volume = Math.Clamp(data.Volume, 0, 1);
            Muted = data.Muted;
            Speed = Math.Clamp(data.Speed is 0 ? 1 : data.Speed, 0.5, 2);
            NormalizeVolume = data.NormalizeVolume;
            Repeat = Math.Clamp(data.Repeat, 0, 2);
            Shuffle = data.Shuffle;
            Autoplay = data.Autoplay;
            PauseHistory = data.PauseHistory;
            ServerUrl = data.ServerUrl;
            LastUpdateCheck = data.LastUpdateCheck;
            PreferSyncedLyrics = data.PreferSyncedLyrics;
            PauseSearchHistory = data.PauseSearchHistory;
            Sorts = data.Sorts ?? [];
            ImageCacheMaxMb = Math.Max(1, data.ImageCacheMaxMb);
            UpdateAnnouncedVersion = data.UpdateAnnouncedVersion;
            WindowPlacement = data.WindowPlacement;
            MiniPlayerPosition = data.MiniPlayerPosition;
            SongCacheSizeChosen = data.SongCacheSizeChosen;
            SongCacheMaxMb = data.SongCacheSizeChosen ? Math.Max(0, data.SongCacheMaxMb) : 4096;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Settings unreadable, defaults used", e);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            var data = new SettingsData
            {
                Theme = Theme, LastSection = LastSection, Volume = Volume, Muted = Muted, Speed = Speed, NormalizeVolume = NormalizeVolume,
                Repeat = Repeat, Shuffle = Shuffle, Autoplay = Autoplay, PauseHistory = PauseHistory, ServerUrl = ServerUrl,
                LastUpdateCheck = LastUpdateCheck, PreferSyncedLyrics = PreferSyncedLyrics, PauseSearchHistory = PauseSearchHistory, Sorts = Sorts,
                ImageCacheMaxMb = ImageCacheMaxMb, SongCacheMaxMb = SongCacheMaxMb, SongCacheSizeChosen = SongCacheSizeChosen, UpdateAnnouncedVersion = UpdateAnnouncedVersion,
                WindowPlacement = WindowPlacement, MiniPlayerPosition = MiniPlayerPosition,
            };
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Settings not saved", e);
        }
    }

    public void SetSort(string screen, string sort)
    {
        Sorts = new Dictionary<string, string>(Sorts) { [screen] = sort };
    }

    private sealed class SettingsData
    {
        public AppTheme Theme { get; set; }
        public string? LastSection { get; set; }
        public double Volume { get; set; } = 0.8;
        public bool Muted { get; set; }
        public double Speed { get; set; } = 1;
        public bool NormalizeVolume { get; set; } = true;
        public int Repeat { get; set; }
        public bool Shuffle { get; set; }
        public bool Autoplay { get; set; } = true;
        public bool PauseHistory { get; set; }
        public string? ServerUrl { get; set; }
        public long LastUpdateCheck { get; set; }
        public bool PreferSyncedLyrics { get; set; } = true;
        public bool PauseSearchHistory { get; set; }
        public Dictionary<string, string>? Sorts { get; set; }
        public long ImageCacheMaxMb { get; set; } = 128;
        public long SongCacheMaxMb { get; set; } = 4096;
        public bool SongCacheSizeChosen { get; set; }
        public string? UpdateAnnouncedVersion { get; set; }
        public string? WindowPlacement { get; set; }
        public string? MiniPlayerPosition { get; set; }
    }
}
