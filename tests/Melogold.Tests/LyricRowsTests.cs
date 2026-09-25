using Melogold.Core.Lyrics;
using Xunit;

namespace Melogold.Tests;

/// <summary>Строки экрана текста (Android <c>LyricsModel.kt</c>): проигрыши от 4 с, заполнители, активная строка.</summary>
public class LyricRowsTests
{
    private static SyncedLyrics Parse(string lrc) => LrcFormat.Parse(lrc)!;

    [Fact]
    public void IntroAndGapsBecomeInterludes()
    {
        var rows = LyricRows.Build(Parse("[00:05.00]One\n[00:07.00]Two\n[00:09.00]\n[00:20.00]Three\n"));
        Assert.Collection(rows,
            r => Assert.Equal((0L, 5000L), (Assert.IsType<LyricRow.Interlude>(r).StartMs, r.EndMs)),
            r => Assert.Equal("One", Assert.IsType<LyricRow.Sung>(r).Line.Text),
            r => Assert.Equal("Two", Assert.IsType<LyricRow.Sung>(r).Line.Text),
            r => Assert.Equal((9000L, 20000L), (Assert.IsType<LyricRow.Interlude>(r).StartMs, r.EndMs)),
            r => Assert.Equal("Three", Assert.IsType<LyricRow.Sung>(r).Line.Text));
    }

    [Fact]
    public void ShortGapsAndFillersDoNotShowDots()
    {
        var rows = LyricRows.Build(Parse("[00:01.00]One\n[00:03.00]♪\n[00:04.00]Two\n[00:06.00]…\n[00:20.00]Three\n"));
        Assert.Equal(["One", "Two", "Three"], rows.OfType<LyricRow.Sung>().Select(r => r.Line.Text));
        // «…» с 6 с до 20 с — проигрыш с начала заполнителя, «♪» на секунду — ничего
        var interlude = Assert.Single(rows.OfType<LyricRow.Interlude>());
        Assert.Equal((6000L, 20000L), (interlude.StartMs, interlude.EndMs));
    }

    [Fact]
    public void ActiveIndexIsLastStartedRow()
    {
        var rows = LyricRows.Build(Parse("[00:01.00]One\n[00:02.00]Two\n[00:03.00]Three\n"));
        Assert.Equal(-1, LyricRows.ActiveIndexAt(rows, 500));
        Assert.Equal(0, LyricRows.ActiveIndexAt(rows, 1000));
        Assert.Equal(1, LyricRows.ActiveIndexAt(rows, 2999));
        Assert.Equal(2, LyricRows.ActiveIndexAt(rows, 60_000));
    }
}
