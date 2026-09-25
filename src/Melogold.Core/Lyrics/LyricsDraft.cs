using System.Text.RegularExpressions;

namespace Melogold.Core.Lyrics;

/// <summary>
/// Строка в редакторе текста (<c>spec/lyrics.md</c>, «Редактор»; Android <c>DraftLine</c>).
/// </summary>
/// <param name="StartMs">начало строки в треке; null — ещё не отмечена</param>
/// <param name="EndMs">свой конец строки — пауза до следующей; null — строку заканчивает следующая</param>
/// <param name="WordStarts">начала слов <see cref="Text"/> (<see cref="LyricsDraft.SplitWords"/>) в режиме слов; пусто — не отмечены</param>
/// <param name="Backing">подпевка под строкой, в скобках: «(у-у)»</param>
public sealed record DraftLine(
    string Text,
    long? StartMs = null,
    long? EndMs = null,
    IReadOnlyList<long?>? WordStarts = null,
    VocalSide Side = VocalSide.Start,
    string? Backing = null,
    string? Language = null)
{
    public IReadOnlyList<long?> WordStarts { get; init; } = WordStarts ?? [];

    public IReadOnlyList<string> Words => LyricsDraft.SplitWords(Text);

    /// <summary>У каждого слова есть начало (режим слов для строки закончен).</summary>
    public bool WordsTimed => Words.Count > 0 && WordStarts.Count == Words.Count && WordStarts.All(s => s is not null);

    /// <summary>Текст, как его показывает редактор: строка, затем подпевка.</summary>
    public string FullText => Backing is null ? Text : $"{Text} {Backing}";

    public bool Equals(DraftLine? other) =>
        other is not null && Text == other.Text && StartMs == other.StartMs && EndMs == other.EndMs && Side == other.Side
        && Backing == other.Backing && Language == other.Language && WordStarts.SequenceEqual(other.WordStarts);

    public override int GetHashCode() => HashCode.Combine(Text, StartMs, EndMs, Side, Backing, Language, WordStarts.Count);
}

/// <summary>
/// Черновик редактора текста (Android <c>LyricsDraft</c>, те же правила): строки, строка (и в режиме слов — слово),
/// которую отметит следующее нажатие, и что отмечается — строки или слова. Каждая операция возвращает новый черновик,
/// поэтому редактор хранит прежние для «Отменить».
/// </summary>
public sealed partial record LyricsDraft(IReadOnlyList<DraftLine> Lines, int Cursor = 0, int WordCursor = 0, LyricsTiming Timing = LyricsTiming.Line, string? Language = null)
{
    /// <summary>Строка без своего конца и без следующей длится столько (как в LRC).</summary>
    public const long DefaultLineMs = 5_000;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(.*\S)\s+(\([^()]*\))\s*$")]
    private static partial Regex BackingSuffix();

    /// <summary>Слова строки, как их отмечает редактор: через пробел, знаки препинания — при слове.</summary>
    public static IReadOnlyList<string> SplitWords(string text) => Whitespace().Split(text.Trim()).Where(w => w.Length > 0).ToList();

    /// <summary>Отмечена хотя бы одна строка — черновик даёт синхронный текст.</summary>
    public bool HasTiming => Lines.Any(l => l.StartMs is not null);

    /// <summary>Отмечены все строки.</summary>
    public bool Complete => Lines.Count > 0 && Lines.All(l => l.StartMs is not null);

    /// <summary>Черновик обычным текстом: строка на строку, подпевка в конце своей строки.</summary>
    public string ToText() => string.Join('\n', Lines.Select(l => l.FullText));

    /// <summary>
    /// Отметить следующую строку (или слово) временем <paramref name="positionMs"/> и перейти дальше. Начавшаяся строка
    /// заканчивает предыдущую, если у той нет своего конца (<see cref="MarkEnd"/>).
    /// </summary>
    public LyricsDraft Mark(long positionMs)
    {
        if (Cursor < 0 || Cursor >= Lines.Count) return this;
        var line = Lines[Cursor];
        var position = Math.Max(0, positionMs);

        if (Timing == LyricsTiming.Line || line.Words.Count == 0)
        {
            var updated = line with { StartMs = position, WordStarts = [] };
            return (this with { Lines = EndingPreviousAt(Replaced(Lines, Cursor, updated), Cursor, position) }).MovedTo(Cursor + 1);
        }

        var words = line.Words;
        var starts = Enumerable.Range(0, words.Count).Select(i => i < line.WordStarts.Count ? line.WordStarts[i] : null).ToList();
        var word = Math.Clamp(WordCursor, 0, words.Count - 1);
        starts[word] = position;
        var marked = line with { StartMs = word == 0 ? position : line.StartMs ?? position, WordStarts = starts };
        var lines = Replaced(Lines, Cursor, marked);
        if (word == 0) lines = EndingPreviousAt(lines, Cursor, position);
        return word < words.Count - 1 ? this with { Lines = lines, WordCursor = word + 1 } : (this with { Lines = lines }).MovedTo(Cursor + 1);
    }

    /// <summary>Закончить последнюю отмеченную строку в <paramref name="positionMs"/>: до следующей — пауза (проигрыш, если долгая).</summary>
    public LyricsDraft MarkEnd(long positionMs)
    {
        var index = Math.Min(Cursor - 1, Lines.Count - 1);
        if (index < 0 || Lines[index] is not { StartMs: { } start } line) return this;
        return this with { Lines = Replaced(Lines, index, line with { EndMs = Math.Max(positionMs, start + 1) }) };
    }

    /// <summary>Сдвинуть начало строки <paramref name="index"/> (и её слова) на <paramref name="deltaMs"/>; время не меньше 0.</summary>
    public LyricsDraft Nudge(int index, long deltaMs)
    {
        if (index < 0 || index >= Lines.Count || Lines[index] is not { StartMs: { } start } line) return this;
        long Moved(long value) => Math.Max(0, value + deltaMs);
        return this with
        {
            Lines = Replaced(Lines, index, line with
            {
                StartMs = Moved(start),
                EndMs = line.EndMs is { } end ? Moved(end) : null,
                WordStarts = line.WordStarts.Select(s => s is { } value ? Moved(value) : (long?)null).ToList(),
            }),
        };
    }

    /// <summary>Всё время на <paramref name="deltaMs"/>: текст со сдвигом начала становится временем трека.</summary>
    public LyricsDraft ShiftedBy(long deltaMs)
    {
        if (deltaMs == 0) return this;
        long Moved(long value) => Math.Max(0, value + deltaMs);
        return this with
        {
            Lines = Lines.Select(line => line with
            {
                StartMs = line.StartMs is { } start ? Moved(start) : null,
                EndMs = line.EndMs is { } end ? Moved(end) : null,
                WordStarts = line.WordStarts.Select(s => s is { } value ? Moved(value) : (long?)null).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Забыть время строки <paramref name="index"/>.</summary>
    public LyricsDraft ClearTiming(int index) =>
        Update(index, line => line with { StartMs = null, EndMs = null, WordStarts = [] });

    /// <summary>Следующая отметка — строка <paramref name="index"/> (с первого слова).</summary>
    public LyricsDraft MovedTo(int index) => this with { Cursor = Math.Clamp(index, 0, Lines.Count), WordCursor = 0 };

    public LyricsDraft WithSide(int index, VocalSide side) => Update(index, line => line with { Side = side });

    public LyricsDraft WithBacking(int index, string? backing) =>
        Update(index, line => line with { Backing = string.IsNullOrWhiteSpace(backing) ? null : InParentheses(backing.Trim()) });

    public LyricsDraft WithLineLanguage(int index, string? language) => Update(index, line => line with { Language = language });

    /// <summary>
    /// Черновик с новым текстом (строка на строку, подпевка в скобках в конце): у строк, текст которых не изменился
    /// (наибольшая общая подпоследовательность), остаются время, сторона и язык; следующая отметка — первая неотмеченная.
    /// </summary>
    public LyricsDraft WithText(string text)
    {
        var fresh = LinesOf(text);
        var matches = MatchUnchanged(Lines.Select(l => l.FullText).ToList(), fresh.Select(l => l.FullText).ToList());
        var merged = fresh.Select((line, index) => matches.TryGetValue(index, out var old) ? Lines[old] with { Text = line.Text, Backing = line.Backing } : line).ToList();
        var firstUntimed = merged.FindIndex(l => l.StartMs is null);
        return (this with { Lines = merged }).MovedTo(firstUntimed >= 0 ? firstUntimed : merged.Count);
    }

    /// <summary>
    /// Синхронный текст отмеченных строк по времени; null — не отмечено ничего. Строка без своего конца длится до
    /// начала следующей (последняя — <see cref="DefaultLineMs"/>).
    /// </summary>
    public SyncedLyrics? ToSyncedLyrics()
    {
        var timed = Lines.Where(l => l.StartMs is not null).OrderBy(l => l.StartMs).ToList();
        if (timed.Count == 0) return null;
        var duet = timed.Any(l => l.Side == VocalSide.End);
        var wordTimed = Timing == LyricsTiming.Word && timed.All(l => l.WordsTimed);

        var lines = timed.Select((line, index) =>
        {
            var start = line.StartMs!.Value;
            var next = index + 1 < timed.Count ? timed[index + 1].StartMs : null;
            var end = Math.Max(line.EndMs ?? next ?? start + DefaultLineMs, start + 1);
            return new SyncedLine
            {
                StartMs = start,
                EndMs = end,
                Text = line.Text,
                Words = wordTimed ? WordsOf(line, end) : [],
                Agent = duet ? AgentOf(line.Side) : null,
                Side = line.Side,
                Language = line.Language,
                Background = line.Backing is { } text ? new BackingVocals(start, end, [new SyncedWord(start, end, text)]) : null,
            };
        }).ToList();

        return new SyncedLyrics(
            lines,
            wordTimed ? LyricsTiming.Word : LyricsTiming.Line,
            duet ? [new LyricsAgent(AgentOf(VocalSide.Start), VocalSide.Start), new LyricsAgent(AgentOf(VocalSide.End), VocalSide.End)] : [],
            Language);
    }

    /// <summary>Черновик из текста: строка на непустую строку, «(…)» в конце строки — подпевка.</summary>
    public static LyricsDraft FromText(string text, string? language = null) => new(LinesOf(text), Language: language);

    /// <summary>Черновик готового синхронного текста — чтобы его править.</summary>
    public static LyricsDraft From(SyncedLyrics lyrics)
    {
        var lines = lyrics.Lines.Select(line => new DraftLine(
            line.Text,
            line.StartMs,
            line.EndMs,
            line.Words.Count > 0 && line.Words.Count == SplitWords(line.Text).Count ? line.Words.Select(w => (long?)w.StartMs).ToList() : [],
            line.Side,
            line.Background?.Text is { Length: > 0 } backing ? InParentheses(backing) : null,
            line.Language)).ToList();
        // Свой конец остаётся только там, где он оставляет паузу: в остальных местах строку заканчивает следующая
        lines = lines.Select((line, index) =>
            line.EndMs is { } end && index + 1 < lines.Count && lines[index + 1].StartMs is { } next && end >= next ? line with { EndMs = null } : line).ToList();
        return new LyricsDraft(lines, lyrics.Lines.Count, 0, lyrics.Timing, lyrics.Language);
    }

    public bool Equals(LyricsDraft? other) =>
        other is not null && Cursor == other.Cursor && WordCursor == other.WordCursor && Timing == other.Timing && Language == other.Language
        && Lines.SequenceEqual(other.Lines);

    public override int GetHashCode() => HashCode.Combine(Cursor, WordCursor, Timing, Language, Lines.Count);

    private LyricsDraft Update(int index, Func<DraftLine, DraftLine> transform) =>
        index < 0 || index >= Lines.Count ? this : this with { Lines = Replaced(Lines, index, transform(Lines[index])) };

    private static string AgentOf(VocalSide side) => side == VocalSide.Start ? "v1" : "v2";

    private static string InParentheses(string text) => text.StartsWith('(') && text.EndsWith(')') ? text : $"({text})";

    private static List<DraftLine> LinesOf(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .Select(l => BackingSuffix().Match(l) is { Success: true } match ? new DraftLine(match.Groups[1].Value, Backing: match.Groups[2].Value) : new DraftLine(l))
        .ToList();

    /// <summary>Слова строки: каждое до начала следующего, последнее — до <paramref name="end"/>.</summary>
    private static List<SyncedWord> WordsOf(DraftLine line, long end)
    {
        var words = line.Words;
        return words.Select((word, index) =>
        {
            var start = line.WordStarts[index]!.Value;
            var wordEnd = Math.Max(index + 1 < words.Count ? line.WordStarts[index + 1]!.Value : end, start + 1);
            return new SyncedWord(start, wordEnd, index < words.Count - 1 ? word + " " : word);
        }).ToList();
    }

    private static List<DraftLine> Replaced(IReadOnlyList<DraftLine> lines, int index, DraftLine value)
    {
        var copy = lines.ToList();
        copy[index] = value;
        return copy;
    }

    /// <summary>Последняя отмеченная строка до <paramref name="index"/> заканчивается в <paramref name="position"/>, если у неё нет своего конца раньше.</summary>
    private static List<DraftLine> EndingPreviousAt(List<DraftLine> lines, int index, long position)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (lines[i].StartMs is null) continue;
            if (lines[i].EndMs is { } end && end <= position) return lines;
            lines[i] = lines[i] with { EndMs = null };
            return lines;
        }
        return lines;
    }

    /// <summary>Для строк нового текста — индекс той же строки в старом, если она в наибольшей общей подпоследовательности.</summary>
    private static Dictionary<int, int> MatchUnchanged(List<string> old, List<string> fresh)
    {
        var lengths = new int[old.Count + 1, fresh.Count + 1];
        for (var i = old.Count - 1; i >= 0; i--)
        for (var j = fresh.Count - 1; j >= 0; j--)
            lengths[i, j] = old[i] == fresh[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var matches = new Dictionary<int, int>();
        int a = 0, b = 0;
        while (a < old.Count && b < fresh.Count)
        {
            if (old[a] == fresh[b]) matches[b++] = a++;
            else if (lengths[a + 1, b] >= lengths[a, b + 1]) a++;
            else b++;
        }
        return matches;
    }
}
