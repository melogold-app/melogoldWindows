using Melogold.Core.Data;

namespace Melogold.Core.Lyrics;

/// <summary>
/// Закреплённый текст (tasks/0012, API §4.8 <c>lyrics.pin.set</c>; Android <c>LyricsPins.kt</c>, Linux <c>pins.rs</c>):
/// прослушал трек 30 с с найденным автоматически текстом и не менял — текст закрепляется ссылкой у поставщика, и все
/// устройства аккаунта показывают его, а не ищут свой. Сервер хранит только ссылку (~150 байт вместо ~5 КБ текста).
/// </summary>
/// <param name="Source"><c>youtube_music | lrclib | kugou</c></param>
/// <param name="Ref">номер текста у поставщика: LrcLib — id записи, YouTube Music — browseId <c>MPLYt…</c>, KuGou — <c>id:accesskey</c></param>
/// <param name="StartTimeMs">сдвиг «позже» — где в треке начинается текст; «раньше» остаётся на устройстве</param>
public sealed record LyricsPin(string Source, string Ref, long? StartTimeMs);

/// <summary>Правила закрепления. Порядок выбора текста трека: свой → закреплённый → поиск → общий с сервера.</summary>
public static class LyricsPins
{
    /// <summary>Столько прослушал трек с найденным автоматически текстом — текст закрепляется.</summary>
    public const long PinAfterMs = 30_000;

    public const int RefMax = 200;
    public const long StartTimeMax = 86_400_000;

    private static readonly string[] Sources = [LyricsSources.YouTubeMusic, LyricsSources.LrcLib, LyricsSources.KuGou];

    private static bool Filled(string? text) => !string.IsNullOrEmpty(text);

    /// <summary>Сдвиг «позже» синхронного текста как <c>startTimeMs</c>; «раньше» (положительный наш сдвиг) — нет.</summary>
    public static long? StartTimeOf(StoredLyrics lyrics) => lyrics.OffsetMs < 0 ? -lyrics.OffsetMs : null;

    /// <summary>Закрепление, как его прислал сервер: неизвестный источник или пустая ссылка — закрепления нет.</summary>
    public static LyricsPin? Of(string? source, string? reference, long? startTimeMs) =>
        source is not null && Sources.Contains(source) && reference?.Trim() is { Length: > 0 } trimmed
            ? new LyricsPin(source, Domain.Utf16.Truncate(trimmed, RefMax), startTimeMs is >= 0 and <= StartTimeMax ? startTimeMs : null)
            : null;

    /// <summary>Показывает ли <paramref name="lyrics"/> текст, на который ссылается закрепление (любая сторона).</summary>
    public static bool Shows(StoredLyrics lyrics, LyricsPin pin) =>
        (Filled(lyrics.Synced) && lyrics.SyncedRef == pin.Ref && lyrics.SyncedSource == pin.Source)
        || (Filled(lyrics.Plain) && lyrics.PlainRef == pin.Ref && lyrics.PlainSource == pin.Source);

    /// <summary>
    /// Закрепление найденного автоматически текста: сторона, которая на экране (синхронная, иначе обычная), её поставщик
    /// и номер у него. null — свой текст, общий с сервера или текст без ссылки.
    /// </summary>
    public static LyricsPin? PinOf(StoredLyrics? lyrics)
    {
        if (lyrics is null || LyricsSyncRules.IsOwn(lyrics)) return null;
        var synced = Filled(lyrics.Synced);
        var (source, reference) = synced ? (lyrics.SyncedSource, lyrics.SyncedRef)
            : Filled(lyrics.Plain) ? (lyrics.PlainSource, lyrics.PlainRef)
            : (null, null);
        return Of(source, reference, synced ? StartTimeOf(lyrics) : null);
    }

    /// <summary>
    /// Достать ли закреплённый текст по ссылке вместо того, что лежит здесь: свой текст важнее закрепления, а текст, который
    /// уже показывает закреплённый, доставать заново незачем.
    /// </summary>
    public static bool NeedsPinned(StoredLyrics? stored, LyricsPin? pin) =>
        pin is not null && (stored is null || (!LyricsSyncRules.IsOwn(stored) && !Shows(stored, pin)));

    /// <summary>Закреплённый текст, как он ложится здесь: найденный автоматически, со сдвигом закрепления.</summary>
    public static StoredLyrics FromPinned(StoredLyrics pinned, LyricsPin pin, StoredLyrics? current) =>
        pinned with { OffsetMs = -(pin.StartTimeMs ?? 0), Language = pinned.Language ?? current?.Language, Chosen = false };

    /// <summary>После сдвига текста: закрепление этого текста забирает сдвиг «позже». null — закрепление не меняется.</summary>
    public static LyricsPin? Shifted(LyricsPin pin, StoredLyrics lyrics)
    {
        if (LyricsSyncRules.IsOwn(lyrics) || !Shows(lyrics, pin) || lyrics.OffsetMs > 0) return null;
        var start = StartTimeOf(lyrics);
        return start != pin.StartTimeMs ? pin with { StartTimeMs = start } : null;
    }
}
