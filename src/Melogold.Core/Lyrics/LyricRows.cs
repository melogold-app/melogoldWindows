namespace Melogold.Core.Lyrics;

/// <summary>Строка экрана синхронного текста: спетая строка или проигрыш (три точки).</summary>
public abstract record LyricRow(long StartMs, long EndMs)
{
    public sealed record Sung(SyncedLine Line) : LyricRow(Line.StartMs, Line.EndMs);

    /// <summary><paramref name="Side"/> — сторона строки после паузы: туда смотрят дальше.</summary>
    public sealed record Interlude(long Start, long End, VocalSide Side) : LyricRow(Start, End);
}

/// <summary>Строки экрана текста (Android <c>LyricsModel.kt</c>): проигрыш — перед первой строкой и в паузах от 4 с.</summary>
public static class LyricRows
{
    /// <summary>Пауза не короче этой перед строкой или между строками показывается проигрышем.</summary>
    public const long InterludeMinGapMs = 4_000;

    private static readonly char[] NoteCharacters = ['♪', '♫', '♬', '♩', '…', '.'];

    private static bool IsFiller(string text) => string.IsNullOrWhiteSpace(text) || text.Trim().All(c => NoteCharacters.Contains(c) || char.IsWhiteSpace(c));

    /// <summary>Спетые строки и проигрыши; строки-заполнители («♪», «…») становятся проигрышем или исчезают.</summary>
    public static List<LyricRow> Build(SyncedLyrics lyrics)
    {
        var sung = lyrics.Lines.Where(l => !IsFiller(l.Text)).OrderBy(l => l.StartMs).ToList();
        var rows = new List<LyricRow>();
        if (sung.Count == 0) return rows;
        if (sung[0].StartMs >= InterludeMinGapMs) rows.Add(new LyricRow.Interlude(0, sung[0].StartMs, sung[0].Side));
        for (var i = 0; i < sung.Count; i++)
        {
            var line = sung[i];
            rows.Add(new LyricRow.Sung(line));
            if (i + 1 >= sung.Count) continue;
            var next = sung[i + 1];
            // Строка-заполнитель между ними заканчивает спетую там, где начинается
            var filler = lyrics.Lines.FirstOrDefault(l => l.StartMs > line.StartMs && l.StartMs < next.StartMs && IsFiller(l.Text));
            var gapStart = filler is not null ? Math.Min(filler.StartMs, line.EndMs) : line.EndMs;
            if (next.StartMs - gapStart >= InterludeMinGapMs) rows.Add(new LyricRow.Interlude(gapStart, next.StartMs, next.Side));
        }
        return rows;
    }

    /// <summary>Индекс последней строки с началом не позже <paramref name="positionMs"/>; -1 до первой.</summary>
    public static int ActiveIndexAt(IReadOnlyList<LyricRow> rows, long positionMs)
    {
        int low = 0, high = rows.Count - 1, result = -1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (rows[mid].StartMs <= positionMs)
            {
                result = mid;
                low = mid + 1;
            }
            else high = mid - 1;
        }
        return result;
    }
}
