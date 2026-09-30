using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Lyrics;
using Melogold.Core.Music;

namespace Melogold.InnerTube.Lyrics;

/// <summary>
/// Итог поиска текста: <paramref name="AnyFailure"/> — хоть один источник не ответил из-за сети (такой пустой итог
/// не кэшируется как «текста нет»).
/// </summary>
/// <summary>Что нашла цепочка; <paramref name="Mine"/> — это своя версия с сервера (своя и здесь).</summary>
/// <param name="SyncedRef">номер синхронного текста у поставщика (закрепление, tasks/0012)</param>
/// <param name="PlainRef">номер обычного текста у поставщика</param>
public sealed record LyricsFetchResult(string? Plain, string? Synced, bool AnyFailure, string? PlainSource, string? SyncedSource, long? OffsetMs = null,
    string? Language = null, bool Mine = false, string? SyncedRef = null, string? PlainRef = null);

/// <summary>
/// Цепочка источников текста (docs/PROMPT.md §8.2, Android <c>LyricsFetcher.kt</c>). Название сначала проходит
/// <see cref="TitleCleaner"/>: названия YouTube («Кино - Группа крови (Official Video)», канал вместо исполнителя) как
/// есть ничего не находят.
/// <para>
/// Синхронный: у песни (есть альбом) — свой timed-текст YouTube Music этого самого трека, потом LRCLIB по очищенному
/// названию и длительности, потом KuGou; у видео LRCLIB первым (видео может идти не в такт песне). Обычный: YouTube
/// Music, потом LRCLIB. Стороны, которые уже есть в <c>current</c>, заново не ищутся.
/// </para>
/// </summary>
public sealed class LyricsFetcher(YouTubeMusic music, LrcLib lrcLib, KuGou kuGou)
{
    public LrcLib LrcLib => lrcLib;

    /// <summary>
    /// Текст с сервера Melogold (docs/LYRICS-SYNC.md §3.5): своя версия или общая версия другого пользователя; null —
    /// нет аккаунта, модуля текстов на сервере или текста. Спрашивается, только если провайдеры не нашли синхронный.
    /// </summary>
    public Func<string, CancellationToken, Task<(LyricsPayload Payload, bool Mine)?>>? Community { get; set; }

    /// <param name="custom">
    /// своё название и исполнитель (tasks/0011): LrcLib и KuGou спрашиваются сначала по ним — у загрузок фанатов по
    /// оригиналу «Artist — Song (live, fan upload)» текста не находится
    /// </param>
    public async Task<LyricsFetchResult> FetchAsync(Track track, long durationMs, StoredLyrics? current, CancellationToken ct = default, TrackOverride? custom = null)
    {
        var rawArtist = track.ArtistsText ?? "";
        var rawTitle = track.Title;
        var isSong = track.AlbumId is not null || track.AlbumTitle is not null;
        var clean = TitleCleaner.Clean(rawTitle, rawArtist.Length == 0 ? null : rawArtist, isSong ? "song" : null);
        var artist = clean.Artist ?? rawArtist;
        var title = clean.Title.Trim().Length > 0 ? clean.Title : rawTitle;
        var anyFailure = false;
        // Варианты запроса: свои названия, очищенные, как у трека
        List<(string Artist, string Title)> names = [];
        if (custom is { IsEmpty: false }) names.Add((custom.ArtistsText ?? artist, custom.Title ?? title));
        names.Add((artist, title));
        names.Add((rawArtist, rawTitle));
        names = names.Distinct().ToList();

        async Task<T?> Try<T>(Func<Task<T?>> call) where T : class
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or YouTubeException or System.Text.Json.JsonException or FormatException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (e is HttpRequestException or TaskCanceledException or YouTubeException { Kind: YouTubeErrorKind.Offline }) anyFailure = true;
                return null;
            }
        }

        // LrcLib: первый вариант запроса, по которому нашёлся текст (запись целиком — с id для закрепления)
        async Task<LrcLibTrack?> LrcLibFirst(bool synced)
        {
            foreach (var (a, t) in names)
                if (await Try(() => lrcLib.BestAsync(a, t, durationMs, synced, ct)).ConfigureAwait(false) is { } found) return found;
            return null;
        }

        static string Id(LrcLibTrack found) => found.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Вкладка «Текст» страницы трека; нет её — у YouTube Music текста нет
        string? browseId = null;
        var browseKnown = false;
        async Task<string?> LyricsBrowseId()
        {
            if (browseKnown) return browseId;
            browseKnown = true;
            browseId = (await Try(() => music.NextAsync(track.VideoId, ct: ct)!).ConfigureAwait(false))?.LyricsBrowseId;
            return browseId;
        }

        var plainSource = current?.PlainSource;
        var plain = current?.Plain;
        var plainRef = current?.PlainRef;
        if (plain is null)
        {
            if (await LyricsBrowseId().ConfigureAwait(false) is { } id && await Try(async () => (await music.LyricsAsync(id, ct).ConfigureAwait(false))?.Text).ConfigureAwait(false) is { } ytm)
            {
                plain = ytm;
                plainSource = LyricsSources.YouTubeMusic;
                plainRef = id;
            }
            else if (await LrcLibFirst(false).ConfigureAwait(false) is { PlainLyrics: { } lrc } found)
            {
                plain = lrc;
                plainSource = LyricsSources.LrcLib;
                plainRef = Id(found);
            }
        }

        async Task<(string Text, string Source, string Ref)?> YouTubeMusicTimed() =>
            await LyricsBrowseId().ConfigureAwait(false) is { } id && await Try(() => music.TimedLyricsAsync(id, ct)).ConfigureAwait(false) is { } text
                ? (text, LyricsSources.YouTubeMusic, id)
                : null;

        async Task<(string Text, string Source, string Ref)?> LrcLibSynced() =>
            await LrcLibFirst(true).ConfigureAwait(false) is { SyncedLyrics: { } text } found ? (text, LyricsSources.LrcLib, Id(found)) : null;

        var syncedSource = current?.SyncedSource;
        var synced = current?.Synced;
        var syncedRef = current?.SyncedRef;
        if (synced is null)
        {
            var found = isSong
                ? await YouTubeMusicTimed().ConfigureAwait(false) ?? await LrcLibSynced().ConfigureAwait(false)
                : await LrcLibSynced().ConfigureAwait(false) ?? await YouTubeMusicTimed().ConfigureAwait(false);
            if (found is null && await Try(() => kuGou.LyricsWithRefAsync(names[0].Artist, names[0].Title, durationMs / 1000, ct)).ConfigureAwait(false) is { } kugou)
                found = (kugou.Text, LyricsSources.KuGou, kugou.Ref);
            if (found is { } hit)
            {
                synced = hit.Text;
                syncedSource = hit.Source;
                syncedRef = hit.Ref;
            }
        }
        long? offset = null;
        string? language = null;
        var mine = false;
        if (synced is null && Community is { } community)
        {
            try
            {
                if (await community(track.VideoId, ct).ConfigureAwait(false) is { } found && found.Payload.Synced is { } text)
                {
                    // Своя версия остаётся своей, общая помечается «сообщество Melogold» и своей не становится
                    synced = text;
                    syncedSource = found.Mine ? found.Payload.SyncedSource : LyricsSources.Melogold;
                    syncedRef = null;
                    if (plain is null && found.Payload.Plain is { } communityPlain)
                    {
                        plain = communityPlain;
                        plainSource = found.Mine ? found.Payload.PlainSource : LyricsSources.Melogold;
                        plainRef = null;
                    }
                    offset = -(found.Payload.StartTimeMs ?? 0);
                    language = found.Payload.Language;
                    mine = found.Mine;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                anyFailure = true;
            }
        }
        return new LyricsFetchResult(plain, synced, anyFailure, plainSource, syncedSource, offset, language, mine, syncedRef, plainRef);
    }

    /// <summary>
    /// Закреплённый текст по ссылке у поставщика (tasks/0012): LrcLib — запись по id, YouTube Music — текст и синхронный
    /// текст по browseId, KuGou — LRC по <c>id:accesskey</c>. Стороны, которых у поставщика нет, — null («ещё не искали»):
    /// их добирает обычный поиск. null — поставщик не ответил или по ссылке ничего нет.
    /// </summary>
    public async Task<StoredLyrics?> FetchPinnedAsync(LyricsPin pin, CancellationToken ct = default)
    {
        try
        {
            switch (pin.Source)
            {
                case LyricsSources.LrcLib:
                    if (!long.TryParse(pin.Ref, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                        || await lrcLib.GetAsync(id, ct).ConfigureAwait(false) is not { } record) return null;
                    var synced = string.IsNullOrWhiteSpace(record.SyncedLyrics) ? null : record.SyncedLyrics;
                    var plain = string.IsNullOrWhiteSpace(record.PlainLyrics) ? null : record.PlainLyrics;
                    return synced is null && plain is null ? null
                        : new StoredLyrics(synced, plain, synced is null ? null : LyricsSources.LrcLib, plain is null ? null : LyricsSources.LrcLib,
                            SyncedRef: synced is null ? null : pin.Ref, PlainRef: plain is null ? null : pin.Ref);
                case LyricsSources.YouTubeMusic:
                    var timed = await music.TimedLyricsAsync(pin.Ref, ct).ConfigureAwait(false);
                    var text = (await music.LyricsAsync(pin.Ref, ct).ConfigureAwait(false))?.Text;
                    return timed is null && text is null ? null
                        : new StoredLyrics(timed, text, timed is null ? null : LyricsSources.YouTubeMusic, text is null ? null : LyricsSources.YouTubeMusic,
                            SyncedRef: timed is null ? null : pin.Ref, PlainRef: text is null ? null : pin.Ref);
                case LyricsSources.KuGou:
                    return await kuGou.ByRefAsync(pin.Ref, ct).ConfigureAwait(false) is { } lrc
                        ? new StoredLyrics(lrc, null, LyricsSources.KuGou, null, SyncedRef: pin.Ref)
                        : null;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or YouTubeException or System.Text.Json.JsonException or FormatException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return null;
        }
    }
}
