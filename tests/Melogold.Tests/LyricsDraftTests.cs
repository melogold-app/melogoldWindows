using Melogold.Core.Lyrics;
using Xunit;

namespace Melogold.Tests;

/// <summary>Редактор текста (<c>spec/lyrics.md</c>, «Редактор»): те же случаи, что Android <c>LyricsDraftTest</c>.</summary>
public class LyricsDraftTests
{
    [Fact]
    public void TextBecomesLinesWithBackingInParenthesesAtTheEnd()
    {
        var draft = LyricsDraft.FromText("First line\n\n  Second line (ooh, ooh)  \nThird (not) backing here\n");
        Assert.Equal(["First line", "Second line", "Third (not) backing here"], draft.Lines.Select(l => l.Text));
        Assert.Equal("(ooh, ooh)", draft.Lines[1].Backing);
        Assert.Null(draft.Lines[2].Backing);
        Assert.Equal("First line\nSecond line (ooh, ooh)\nThird (not) backing here", draft.ToText());
    }

    [Fact]
    public void MarksTimeLinesInOrderAndALineEndsWhereTheNextStarts()
    {
        var draft = LyricsDraft.FromText("One\nTwo\nThree").Mark(1_000).Mark(4_000).Mark(8_000);
        Assert.Equal(3, draft.Cursor);
        Assert.True(draft.Complete);
        var lyrics = draft.ToSyncedLyrics()!;
        Assert.Equal(LyricsTiming.Line, lyrics.Timing);
        Assert.Equal([(1_000L, 4_000L), (4_000L, 8_000L), (8_000L, 13_000L)], lyrics.Lines.Select(l => (l.StartMs, l.EndMs)));
    }

    [Fact]
    public void AnExplicitEndLeavesAGap()
    {
        var lyrics = LyricsDraft.FromText("One\nTwo").Mark(1_000).MarkEnd(3_000).Mark(9_000).ToSyncedLyrics()!;
        Assert.Equal((1_000L, 3_000L), (lyrics.Lines[0].StartMs, lyrics.Lines[0].EndMs));
        Assert.Equal(9_000, lyrics.Lines[1].StartMs);
    }

    [Fact]
    public void RemarkingEarlierThanAnEndKeepsOnlyRealGaps()
    {
        var draft = LyricsDraft.FromText("One\nTwo").Mark(1_000).MarkEnd(3_000).Mark(2_000);
        Assert.Null(draft.Lines[0].EndMs);
        var line = draft.ToSyncedLyrics()!.Lines[0];
        Assert.Equal((1_000L, 2_000L), (line.StartMs, line.EndMs));
    }

    [Fact]
    public void WordModeTimesEveryWordThenMovesToTheNextLine()
    {
        var draft = (LyricsDraft.FromText("a b c\nd", "en") with { Timing = LyricsTiming.Word }).Mark(100).Mark(200).Mark(300);
        Assert.Equal(1, draft.Cursor);
        Assert.Equal(0, draft.WordCursor);
        var lyrics = draft.Mark(1_000).ToSyncedLyrics()!;
        Assert.Equal(LyricsTiming.Word, lyrics.Timing);
        Assert.Equal("en", lyrics.Language);
        Assert.Equal([new SyncedWord(100, 200, "a "), new SyncedWord(200, 300, "b "), new SyncedWord(300, 1_000, "c")], lyrics.Lines[0].Words);
    }

    [Fact]
    public void LinesOnlyPartlyTimedByWordFallBackToLineTiming()
    {
        var lyrics = (LyricsDraft.FromText("a b\nc") with { Timing = LyricsTiming.Word }).Mark(100).MovedTo(1).Mark(2_000).ToSyncedLyrics()!;
        Assert.Equal(LyricsTiming.Line, lyrics.Timing);
        Assert.All(lyrics.Lines, l => Assert.Empty(l.Words));
    }

    [Fact]
    public void ALineOnTheEndSideMakesADuet()
    {
        var lyrics = LyricsDraft.FromText("One\nTwo").Mark(0).Mark(2_000).WithSide(1, VocalSide.End).ToSyncedLyrics()!;
        Assert.Equal(["v1", "v2"], lyrics.Agents.Select(a => a.Id));
        Assert.Equal(["v1", "v2"], lyrics.Lines.Select(l => l.Agent));
        Assert.True(lyrics.IsDuet);
    }

    [Fact]
    public void EditingTheTextKeepsTheTimingOfUnchangedLines()
    {
        var draft = LyricsDraft.FromText("One\nTwo\nThree").Mark(1_000).Mark(2_000).Mark(3_000).WithText("One\nNew line\nTwo\nThree (yeah)");
        Assert.Equal([1_000L, null, 2_000L, null], draft.Lines.Select(l => l.StartMs));
        Assert.Equal("(yeah)", draft.Lines[3].Backing);
        Assert.Equal(1, draft.Cursor);
    }

    [Fact]
    public void NudgingMovesALineAndItsWords()
    {
        var draft = (LyricsDraft.FromText("a b") with { Timing = LyricsTiming.Word }).Mark(1_000).Mark(1_500).Nudge(0, -100);
        Assert.Equal(900, draft.Lines[0].StartMs);
        Assert.Equal([900L, 1_400L], draft.Lines[0].WordStarts);
        Assert.Equal(0, draft.Nudge(0, -5_000).Lines[0].StartMs);
    }

    [Fact]
    public void AStartOffsetShiftsEveryTime()
    {
        var draft = (LyricsDraft.FromText("a b\nc") with { Timing = LyricsTiming.Word }).Mark(0).Mark(500).Mark(2_000).ShiftedBy(1_500);
        Assert.Equal([1_500L, 3_500L], draft.Lines.Select(l => l.StartMs));
        Assert.Equal([1_500L, 2_000L], draft.Lines[0].WordStarts);
    }

    [Fact]
    public void SyncedLyricsRoundTripThroughTheDraftAndTtml()
    {
        var lyrics = (LyricsDraft.FromText("Hello there\nGeneral Kenobi (you are a bold one)") with { Timing = LyricsTiming.Word, Language = "en" })
            .Mark(1_000).Mark(1_600).Mark(3_000).Mark(3_700)
            .WithSide(1, VocalSide.End)
            .ToSyncedLyrics()!;

        var parsed = TtmlFormat.Parse(TtmlFormat.Write(lyrics))!;
        Assert.Equal(lyrics.Lines.Select(l => l.Text), parsed.Lines.Select(l => l.Text));
        Assert.Equal(lyrics.Lines.SelectMany(l => l.Words), parsed.Lines.SelectMany(l => l.Words));
        Assert.Equal("(you are a bold one)", parsed.Lines[1].Background?.Text);
        Assert.Equal(VocalSide.End, parsed.Lines[1].Side);

        var again = LyricsDraft.From(parsed);
        Assert.Equal([1_000L, 3_000L], again.Lines.Select(l => l.StartMs));
        Assert.Equal([3_000L, 3_700L], again.Lines[1].WordStarts);
        Assert.Equal("(you are a bold one)", again.Lines[1].Backing);
        Assert.Equal(2, again.Cursor);
    }

    [Fact]
    public void UndoCompareSeesEqualDrafts()
    {
        var a = LyricsDraft.FromText("One\nTwo").Mark(100);
        var b = LyricsDraft.FromText("One\nTwo").Mark(100);
        Assert.Equal(a, b);
        Assert.NotEqual(a, a.Mark(200));
    }
}
