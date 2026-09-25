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

    /// <summary>Сдвиг синхронного текста, мс.</summary>
    [ObservableProperty]
    public partial int LyricsOffsetMs { get; set; }

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
            LyricsOffsetMs = data.LyricsOffsetMs;
            Sorts = data.Sorts ?? [];
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
                LastUpdateCheck = LastUpdateCheck, LyricsOffsetMs = LyricsOffsetMs, Sorts = Sorts,
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
        public int LyricsOffsetMs { get; set; }
        public Dictionary<string, string>? Sorts { get; set; }
    }
}
