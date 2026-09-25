using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Lyrics;
using Melogold.Core.Music;

namespace Melogold.InnerTube.Lyrics;

/// <summary>
/// Итог поиска текста: <paramref name="AnyFailure"/> — хоть один источник не ответил из-за сети (такой пустой итог
/// не кэшируется как «текста нет»).
/// </summary>
public sealed record LyricsFetchResult(string? Plain, string? Synced, bool AnyFailure, string? PlainSource, string? SyncedSource, long? OffsetMs = null, string? Language = null);

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

    public async Task<LyricsFetchResult> FetchAsync(Track track, long durationMs, StoredLyrics? current, CancellationToken ct = default)
    {
        var rawArtist = track.ArtistsText ?? "";
        var rawTitle = track.Title;
        var isSong = track.AlbumId is not null || track.AlbumTitle is not null;
        var clean = TitleCleaner.Clean(rawTitle, rawArtist.Length == 0 ? null : rawArtist, isSong ? "song" : null);
        var artist = clean.Artist ?? rawArtist;
        var title = clean.Title.Trim().Length > 0 ? clean.Title : rawTitle;
        var anyFailure = false;

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
        if (plain is null)
        {
            if (await LyricsBrowseId().ConfigureAwait(false) is { } id && await Try(async () => (await music.LyricsAsync(id, ct).ConfigureAwait(false))?.Text).ConfigureAwait(false) is { } ytm)
            {
                plain = ytm;
                plainSource = LyricsSources.YouTubeMusic;
            }
            else if (await Try(() => lrcLib.BestLyricsAsync(artist, title, durationMs, false, ct)).ConfigureAwait(false) is { } lrc)
            {
                plain = lrc;
                plainSource = LyricsSources.LrcLib;
            }
        }

        async Task<string?> YouTubeMusicTimed() =>
            await LyricsBrowseId().ConfigureAwait(false) is { } id ? await Try(() => music.TimedLyricsAsync(id, ct)).ConfigureAwait(false) : null;

        async Task<string?> LrcLibSynced() =>
            await Try(() => lrcLib.BestLyricsAsync(artist, title, durationMs, true, ct)).ConfigureAwait(false)
            // Название как у трека, если очистка его изменила
            ?? (artist != rawArtist || title != rawTitle ? await Try(() => lrcLib.BestLyricsAsync(rawArtist, rawTitle, durationMs, true, ct)).ConfigureAwait(false) : null);

        var syncedSource = current?.SyncedSource;
        var synced = current?.Synced;
        if (synced is null)
        {
            (string? Text, string Source) found = isSong
                ? await YouTubeMusicTimed().ConfigureAwait(false) is { } a ? (a, LyricsSources.YouTubeMusic)
                    : await LrcLibSynced().ConfigureAwait(false) is { } b ? (b, LyricsSources.LrcLib) : (null, "")
                : await LrcLibSynced().ConfigureAwait(false) is { } c ? (c, LyricsSources.LrcLib)
                    : await YouTubeMusicTimed().ConfigureAwait(false) is { } d ? (d, LyricsSources.YouTubeMusic) : (null, "");
            if (found.Text is null && await Try(() => kuGou.LyricsAsync(artist, title, durationMs / 1000, ct)).ConfigureAwait(false) is { } kugou)
                found = (kugou, LyricsSources.KuGou);
            if (found.Text is not null)
            {
                synced = found.Text;
                syncedSource = found.Source;
            }
        }
        long? offset = null;
        string? language = null;
        if (synced is null && Community is { } community)
        {
            try
            {
                if (await community(track.VideoId, ct).ConfigureAwait(false) is { } found && found.Payload.Synced is { } text)
                {
                    // Своя версия остаётся своей, общая помечается «сообщество Melogold» и своей не становится
                    synced = text;
                    syncedSource = found.Mine ? found.Payload.SyncedSource : LyricsSources.Melogold;
                    if (plain is null && found.Payload.Plain is { } communityPlain)
                    {
                        plain = communityPlain;
                        plainSource = found.Mine ? found.Payload.PlainSource : LyricsSources.Melogold;
                    }
                    offset = -(found.Payload.StartTimeMs ?? 0);
                    language = found.Payload.Language;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                anyFailure = true;
            }
        }
        return new LyricsFetchResult(plain, synced, anyFailure, plainSource, syncedSource, offset, language);
    }
}
