namespace Melogold.Core.Lyrics;

/// <summary>Точность времени текста: строки целиком или каждое слово (слог).</summary>
public enum LyricsTiming
{
    Line,
    Word,
}

/// <summary>Сторона голоса в дуэте: первый исполнитель у начального края, второй — у конечного (в RTL зеркально).</summary>
public enum VocalSide
{
    Start,
    End,
}

/// <summary>Исполнитель дуэта (<c>ttm:agent</c>); сторона — по порядку объявления или появления.</summary>
public sealed record LyricsAgent(string Id, VocalSide Side, string? Name = null);

/// <summary>Слово или слог со временем. Пробел после слова входит в текст: склейка слов строки даёт строку.</summary>
public sealed record SyncedWord(long StartMs, long EndMs, string Text);

/// <summary>Подпевка строки (<c>ttm:role="x-bg"</c>): мельче, под основной строкой.</summary>
public sealed record BackingVocals(long StartMs, long EndMs, IReadOnlyList<SyncedWord> Words)
{
    public string Text => string.Concat(Words.Select(w => w.Text)).Trim();
}

/// <summary>Строка синхронного текста.</summary>
public sealed record SyncedLine
{
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required string Text { get; init; }

    /// <summary>Слова со временем; пусто, если время есть только у строки.</summary>
    public IReadOnlyList<SyncedWord> Words { get; init; } = [];

    /// <summary>Исполнитель (<see cref="SyncedLyrics.Agents"/>); null, если текст не говорит.</summary>
    public string? Agent { get; init; }

    public VocalSide Side { get; init; } = VocalSide.Start;

    /// <summary>BCP 47, если отличается от языка всего текста или уточняет его.</summary>
    public string? Language { get; init; }

    public BackingVocals? Background { get; init; }
    public string? Translation { get; init; }
    public string? Transliteration { get; init; }
}

/// <summary>
/// Синхронный текст в модели Melogold (<c>spec/lyrics.md</c>, Android <c>SyncedLyrics.kt</c>): строки, время слов,
/// стороны дуэта, подпевка, языки, переводы. Читается из LRC, расширенного LRC и TTML, пишется в TTML и LRC.
/// </summary>
public sealed record SyncedLyrics(IReadOnlyList<SyncedLine> Lines, LyricsTiming Timing, IReadOnlyList<LyricsAgent> Agents, string? Language = null)
{
    public bool IsDuet => Lines.Any(l => l.Side == VocalSide.End);

    /// <summary>Стороны по порядку появления: первый — у начала, второй — у конца, дальше по очереди.</summary>
    internal static List<LyricsAgent> AssignSides(IEnumerable<string> agentIds) =>
        agentIds.Distinct().Select((id, index) => new LyricsAgent(id, index % 2 == 0 ? VocalSide.Start : VocalSide.End)).ToList();
}
