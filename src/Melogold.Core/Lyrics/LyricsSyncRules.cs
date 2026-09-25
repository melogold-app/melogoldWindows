using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Melogold.Core.Data;

namespace Melogold.Core.Lyrics;

/// <summary>Текст трека в форме сервера (<c>LyricsPut</c> и <c>LyricsText</c>, docs/LYRICS-SYNC.md §2).</summary>
public sealed record LyricsPayload(
    string? Plain,
    string? PlainSource,
    string? Synced,
    string? SyncedFormat,
    string? SyncedSource,
    long? StartTimeMs,
    string? Language);

/// <summary>Что отправить на сервер по итогам сравнения со снимком.</summary>
public abstract record LyricsSend(string VideoId)
{
    /// <summary>Своего текста нет в снимке или он изменился: <c>PUT</c>.</summary>
    public sealed record Put(string VideoId, LyricsPayload Payload, string Hash) : LyricsSend(VideoId);

    /// <summary>Свой текст был на сервере, а здесь его больше нет: <c>DELETE</c>.</summary>
    public sealed record Delete(string VideoId) : LyricsSend(VideoId);

    /// <summary>Текст сервер не принял (слишком большой), и здесь его больше нет: просто забыть.</summary>
    public sealed record Forget(string VideoId) : LyricsSend(VideoId);
}

/// <summary>Что снимок знает о своей версии на сервере: <c>rev</c> и хэш содержимого; <c>rev</c> -1 — сервер отказал (413, 400) или текст длиннее лимита.</summary>
public sealed record LyricsSnapshot(long Rev, string Hash)
{
    public const long Rejected = -1;
}

/// <summary>
/// Правила синхронизации текстов (docs/LYRICS-SYNC.md §3), одинаковые с Android. Свой текст — строка, у которой хотя
/// бы одна сторона из источника <c>user</c> или <c>file</c>; он уходит на сервер целиком, обеими сторонами. Готовый
/// результат LRCLIB, найденные провайдерами тексты и общий текст сообщества (<c>melogold</c>) своими не считаются.
/// </summary>
public static class LyricsSyncRules
{
    /// <summary>Лимиты сервера (API §4.10, в единицах UTF-16).</summary>
    public const int PlainMax = 50_000;
    public const int SyncedMax = 200_000;
    public const int LanguageMax = 35;
    public const long StartTimeMaxMs = 86_400_000;

    private static bool IsOwnSource(string? source) => source is LyricsSources.User or LyricsSources.File;

    /// <summary>Источники, которые принимает сервер; общий текст (<c>melogold</c>) уходит без источника.</summary>
    private static string? ServerSource(string? source) =>
        source is LyricsSources.User or LyricsSources.File or LyricsSources.YouTubeMusic or LyricsSources.LrcLib or LyricsSources.KuGou ? source : null;

    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static bool IsOwn(StoredLyrics lyrics) =>
        (IsOwnSource(lyrics.SyncedSource) && Text(lyrics.Synced) is not null) || (IsOwnSource(lyrics.PlainSource) && Text(lyrics.Plain) is not null);

    /// <summary>
    /// Содержимое для <c>PUT</c>: пустые стороны не отправляются, источник — только вместе со своей стороной, формат — по
    /// содержимому (LRC или TTML), сдвиг — как <c>startTimeMs</c> (где в треке начинается текст: минус наш сдвиг «раньше»).
    /// Сервер принимает только начало не раньше нуля: сдвиг «раньше» остаётся на этом устройстве.
    /// </summary>
    public static LyricsPayload ToPayload(StoredLyrics lyrics)
    {
        var synced = Text(lyrics.Synced);
        var format = synced is null ? null : LyricsFormats.Detect(synced) switch
        {
            LyricsFormats.Format.Ttml => "ttml",
            LyricsFormats.Format.Lrc => "lrc",
            _ => null,
        };
        // Синхронный текст, который не разбирается ни как LRC, ни как TTML, серверу не нужен
        if (format is null) synced = null;
        var plain = Text(lyrics.Plain);
        return new LyricsPayload(
            plain,
            plain is null ? null : ServerSource(lyrics.PlainSource),
            synced,
            format,
            synced is null ? null : ServerSource(lyrics.SyncedSource),
            synced is null || lyrics.OffsetMs >= 0 ? null : Math.Min(-lyrics.OffsetMs, StartTimeMaxMs),
            Text(lyrics.Language) is { Length: <= LanguageMax } language ? language : null);
    }

    /// <summary>Сторона длиннее лимита сервера: такой текст не отправляется, он остаётся только здесь.</summary>
    public static bool TooLarge(LyricsPayload payload) => payload.Plain?.Length > PlainMax || payload.Synced?.Length > SyncedMax;

    /// <summary>
    /// Версия с сервера совпадает с тем, что уже лежит здесь (в том числе эхо своей же отправки): строку не
    /// перезаписывать, чтобы не потерять то, что сервер не хранит (сдвиг «раньше», подпись общего текста).
    /// </summary>
    public static bool SameContent(StoredLyrics? local, StoredLyrics incoming) =>
        local is not null && Hash(ToPayload(local)) == Hash(ToPayload(incoming));

    /// <summary>Версия с сервера — как строка здесь: отсутствующая сторона — «искали, нет» (пустая), источники как есть.</summary>
    public static StoredLyrics FromPayload(LyricsPayload payload) => new(
        payload.Synced ?? "",
        payload.Plain ?? "",
        payload.Synced is null ? null : payload.SyncedSource,
        payload.Plain is null ? null : payload.PlainSource,
        -(payload.StartTimeMs ?? 0),
        payload.Language);

    /// <summary>SHA-256 полей <see cref="LyricsPayload"/> в постоянном порядке.</summary>
    public static string Hash(LyricsPayload payload)
    {
        static string Field(string? value) => value is null ? "\u0000" : value;
        var text = string.Join('\u001f',
            Field(payload.Plain), Field(payload.PlainSource), Field(payload.Synced), Field(payload.SyncedFormat), Field(payload.SyncedSource),
            payload.StartTimeMs?.ToString(CultureInfo.InvariantCulture) ?? "\u0000", Field(payload.Language));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Отправка (§3.3.1): свой текст, которого нет в снимке или чей хэш изменился, — <c>PUT</c>; строка снимка, у
    /// которой больше нет своего текста, — <c>DELETE</c>. Отклонённый сервером текст повторно не отправляется, пока не
    /// изменится.
    /// </summary>
    public static List<LyricsSend> PlanSends(IReadOnlyDictionary<string, StoredLyrics> own, IReadOnlyDictionary<string, LyricsSnapshot> snapshot)
    {
        var sends = new List<LyricsSend>();
        foreach (var (videoId, lyrics) in own.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var payload = ToPayload(lyrics);
            if (payload.Plain is null && payload.Synced is null) continue;
            var hash = Hash(payload);
            if (snapshot.TryGetValue(videoId, out var known) && known.Hash == hash) continue;
            sends.Add(new LyricsSend.Put(videoId, payload, hash));
        }
        foreach (var (videoId, known) in snapshot.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (own.ContainsKey(videoId)) continue;
            sends.Add(known.Rev == LyricsSnapshot.Rejected ? new LyricsSend.Forget(videoId) : new LyricsSend.Delete(videoId));
        }
        return sends;
    }

    /// <summary>
    /// Надгробие с сервера (§3.3.2): свой текст здесь удаляется, только если он не менялся с прошлого синка (хэш равен
    /// снимку). Изменённый остаётся и уйдёт на сервер следующей отправкой.
    /// </summary>
    public static bool DeleteOnTombstone(StoredLyrics? local, LyricsSnapshot? snapshot) =>
        local is not null && IsOwn(local) && snapshot is not null && Hash(ToPayload(local)) == snapshot.Hash;
}
