using Melogold.Core.Data;
using Melogold.Core.Lyrics;
using Melogold.Core.Music;
using Melogold.InnerTube.Lyrics;
using Melogold.Playback;
using Microsoft.UI.Dispatching;

namespace Melogold.App.Services;

/// <summary>Что показывает область текста (Android <c>LyricsContent</c>).</summary>
public abstract record LyricsState
{
    /// <summary>Ничего не известно и ничего не ищется (трека нет, экран текста закрыт).</summary>
    public sealed record Unknown : LyricsState;

    public sealed record Loading : LyricsState;

    /// <param name="OffsetMs">сдвиг у этого трека: положительный — текст раньше</param>
    public sealed record Synced(SyncedLyrics Lyrics, IReadOnlyList<LyricRow> Rows, long OffsetMs, string? Source) : LyricsState;

    public sealed record Plain(string Text, string? Source) : LyricsState;

    /// <summary>Источники ответили, и текста нет.</summary>
    public sealed record NotFound : LyricsState;

    /// <summary>Не удалось из-за сети; в кэш ничего не записано.</summary>
    public sealed record Failed : LyricsState;
}

/// <summary>
/// Текст текущего трека (Android <c>PlayerLyricsState</c>): из кэша библиотеки, недостающие стороны — через
/// <see cref="LyricsFetcher"/>, пока текст на экране. Неудача из-за сети не кэшируется как «текста нет».
/// Здесь же — выбор синхронного или обычного, сдвиг, текст из LRCLIB или из файла.
/// </summary>
public sealed class LyricsService
{
    private readonly PlayerEngine _engine;
    private readonly Library _library;
    private readonly LyricsFetcher _fetcher;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _fetch;
    private bool _active;

    public LyricsService(PlayerEngine engine, Library library, LyricsFetcher fetcher, SettingsStore settings)
    {
        _engine = engine;
        _library = library;
        _fetcher = fetcher;
        _settings = settings;
        _engine.TrackChanged += () => _dispatcher.TryEnqueue(Reload);
    }

    public LyricsState State { get; private set; } = new LyricsState.Unknown();

    public Track? Track { get; private set; }

    /// <summary>Что лежит в кэше для текущего трека (для «Найти текст», сдвига и переключателя).</summary>
    public StoredLyrics? Stored { get; private set; }

    public event Action? Changed;

    public LrcLib LrcLib => _fetcher.LrcLib;

    /// <summary>Текст на экране: только тогда недостающее ищется в сети.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value) Reload();
        }
    }

    /// <summary>Синхронный текст есть — переключателю есть на что переключаться.</summary>
    public bool HasSynced => Stored?.Synced is { Length: > 0 } text && LyricsFormats.ParseSynced(text) is not null;

    public bool HasPlain => Stored?.Plain is { Length: > 0 } || HasSynced;

    public void Retry() => Reload();

    public void Reload()
    {
        _fetch?.Cancel();
        Track = _engine.Current;
        if (Track is not { } track)
        {
            Stored = null;
            Set(new LyricsState.Unknown());
            return;
        }
        var cancel = _fetch = new CancellationTokenSource();
        _ = LoadAsync(track, cancel.Token);
    }

    private async Task LoadAsync(Track track, CancellationToken ct)
    {
        var stored = await Task.Run(() => _library.GetLyrics(track.VideoId), ct);
        if (ct.IsCancellationRequested) return;
        Stored = stored;
        if (_active && (stored?.Plain is null || stored.Synced is null))
        {
            Set(new LyricsState.Loading());
            LyricsFetchResult result;
            try
            {
                var duration = (long)_engine.Duration.TotalMilliseconds;
                result = await Task.Run(() => _fetcher.FetchAsync(track, duration > 0 ? duration : track.DurationMs ?? 0, stored, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (ct.IsCancellationRequested) return;
            var fetched = new StoredLyrics(result.Synced, result.Plain, result.SyncedSource, result.PlainSource,
                result.OffsetMs ?? stored?.OffsetMs ?? 0, result.Language ?? stored?.Language);
            // Недостающая сторона, которую не удалось получить из-за сети, не кэшируется как «нет текста»
            if (result.AnyFailure && (fetched.Plain is null || fetched.Synced is null))
            {
                Stored = fetched;
                var partial = Content(fetched);
                Set(partial is LyricsState.Synced or LyricsState.Plain ? partial : new LyricsState.Failed());
                return;
            }
            Stored = fetched with { Synced = fetched.Synced ?? "", Plain = fetched.Plain ?? "" };
            _ = Task.Run(() => _library.SaveLyrics(track.VideoId, Stored), CancellationToken.None);
        }
        Set(Content(Stored));
    }

    private LyricsState Content(StoredLyrics? stored)
    {
        var synced = stored?.Synced is { Length: > 0 } text ? LyricsFormats.ParseSynced(text) : null;
        var rows = synced is null ? null : LyricRows.Build(synced);
        if (_settings.PreferSyncedLyrics && synced is not null && rows is { Count: > 0 })
            return new LyricsState.Synced(synced, rows, stored!.OffsetMs, stored.SyncedSource);
        if (stored?.Plain is { Length: > 0 } plain) return new LyricsState.Plain(plain, stored.PlainSource);
        if (synced is not null && rows is { Count: > 0 })
            return new LyricsState.Plain(string.Join('\n', rows.OfType<LyricRow.Sung>().Select(r => r.Line.Text)), stored!.SyncedSource);
        if (stored is { Plain: not null, Synced: not null }) return new LyricsState.NotFound();
        return _active ? new LyricsState.Loading() : new LyricsState.Unknown();
    }

    private void Set(LyricsState state)
    {
        State = state;
        Changed?.Invoke();
    }

    /// <summary>Синхронный ↔ обычный: подпись переключателя — по тому, что на экране.</summary>
    public void ToggleSynced()
    {
        _settings.PreferSyncedLyrics = State is not LyricsState.Synced;
        Set(Content(Stored));
    }

    /// <summary>Сдвиг синхронного текста этого трека: положительный — раньше.</summary>
    public void Shift(long deltaMs) => Update(Stored! with { OffsetMs = Stored!.OffsetMs + deltaMs });

    public void ResetShift() => Update(Stored! with { OffsetMs = 0 });

    /// <summary>Текст, выбранный в LRCLIB.</summary>
    public void UseLrcLib(LrcLibTrack found)
    {
        var current = Stored ?? new StoredLyrics(null, null, null, null);
        Update(new StoredLyrics(
            string.IsNullOrWhiteSpace(found.SyncedLyrics) ? current.Synced : found.SyncedLyrics,
            string.IsNullOrWhiteSpace(found.PlainLyrics) ? current.Plain : found.PlainLyrics,
            string.IsNullOrWhiteSpace(found.SyncedLyrics) ? current.SyncedSource : LyricsSources.LrcLib,
            string.IsNullOrWhiteSpace(found.PlainLyrics) ? current.PlainSource : LyricsSources.LrcLib,
            string.IsNullOrWhiteSpace(found.SyncedLyrics) ? current.OffsetMs : 0));
        _settings.PreferSyncedLyrics = !string.IsNullOrWhiteSpace(found.SyncedLyrics);
        Set(Content(Stored));
    }

    /// <summary>Текст из файла: TTML или LRC — синхронный, иначе обычный. false — в файле нет текста.</summary>
    public bool Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var current = Stored ?? new StoredLyrics(null, null, null, null);
        if (LyricsFormats.ParseSynced(text) is { } parsed)
        {
            // Импорт файла — свой текст: через 2 с он уходит на сервер (docs/LYRICS-SYNC.md §3.2)
            Update(current with { Synced = text, SyncedSource = LyricsSources.File, OffsetMs = 0, Language = parsed.Language ?? current.Language });
            _settings.PreferSyncedLyrics = true;
        }
        else if (LyricsFormats.Detect(text) == LyricsFormats.Format.Plain) Update(current with { Plain = text.Trim(), PlainSource = LyricsSources.File });
        else return false;
        Set(Content(Stored));
        return true;
    }

    private void Update(StoredLyrics lyrics)
    {
        if (Track is not { } track) return;
        Stored = lyrics;
        _ = Task.Run(() => _library.SaveLyrics(track.VideoId, lyrics));
        Set(Content(lyrics));
    }
}
