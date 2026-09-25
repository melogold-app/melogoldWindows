using Melogold.Core.Domain;
using Melogold.Core.Music;
using Xunit;

namespace Melogold.Tests;

/// <summary>Правила очереди REWRITE §4.10.4 Android.</summary>
public class PlayQueueTests
{
    private static Track T(string id) => new() { VideoId = id.PadRight(11, '_'), Title = id };

    private static string[] Titles(PlayQueue queue) => queue.Ordered.Select(i => i.Track.Title).ToArray();

    [Fact]
    public void ListStartsAtChosenTrack()
    {
        var queue = new PlayQueue();
        queue.SetList([T("a"), T("b"), T("c")], 1, false);
        Assert.Equal("b", queue.CurrentItem!.Track.Title);
        Assert.True(queue.MoveNext(false));
        Assert.Equal("c", queue.CurrentItem!.Track.Title);
        Assert.False(queue.MoveNext(false));
    }

    [Fact]
    public void PlayNextGoesRightAfterCurrentAndAddToEndBeforeAutoplay()
    {
        var queue = new PlayQueue();
        queue.SetSingle(T("a"));
        queue.AppendAutoplay([T("x"), T("y")]);
        queue.AddToEnd([T("end")]);
        queue.PlayNext([T("next")]);
        Assert.Equal(["a", "next", "end", "x", "y"], Titles(queue));
        Assert.False(queue.Items[2].FromAutoplay);
        Assert.True(queue.Items[3].FromAutoplay);
    }

    [Fact]
    public void ShuffleKeepsCurrentFirstAndAutoplayLast()
    {
        var queue = new PlayQueue();
        queue.SetList([T("a"), T("b"), T("c"), T("d"), T("e")], 2, false);
        queue.AppendAutoplay([T("x"), T("y")]);
        queue.Shuffle(true, new Random(1));
        var order = Titles(queue);
        Assert.Equal("c", order[0]);
        Assert.Equal(["x", "y"], order[^2..]);
        Assert.Equal(["a", "b", "d", "e"], order[1..5].Order().ToArray());
        queue.Shuffle(false);
        Assert.Equal(["a", "b", "c", "d", "e", "x", "y"], Titles(queue));
        Assert.Equal("c", queue.CurrentItem!.Track.Title);
    }

    [Fact]
    public void RepeatAllDropsAutoplayAndWraps()
    {
        var queue = new PlayQueue();
        queue.SetList([T("a"), T("b")], 1, false);
        queue.AppendAutoplay([T("x")]);
        queue.SetRepeat(RepeatMode.All);
        Assert.Equal(["a", "b"], Titles(queue));
        Assert.True(queue.MoveNext(false));
        Assert.Equal("a", queue.CurrentItem!.Track.Title);
        queue.AppendAutoplay([T("y")]);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void RepeatOneRepeatsOnlyAutomatically()
    {
        var queue = new PlayQueue();
        queue.SetList([T("a"), T("b")], 0, false);
        queue.SetRepeat(RepeatMode.One);
        Assert.Equal(0, queue.PeekNext(userAction: false));
        Assert.Equal(1, queue.PeekNext(userAction: true));
    }

    [Fact]
    public void DraggingAutoplayItemUpMakesItUsers()
    {
        var queue = new PlayQueue();
        queue.SetSingle(T("a"));
        queue.AppendAutoplay([T("x"), T("y")]);
        var y = queue.Items[2];
        queue.Move(y.Id, 1);
        Assert.Equal(["a", "y", "x"], Titles(queue));
        Assert.False(queue.Items[1].FromAutoplay);
        Assert.Equal(2, queue.AutoplayStart);
    }

    [Fact]
    public void RemoveKeepsCurrentAndSnapshotRestores()
    {
        var queue = new PlayQueue();
        queue.SetList([T("a"), T("b"), T("c")], 1, false);
        var snapshot = queue.Snapshot(1234);
        queue.Remove(queue.Items[0].Id);
        Assert.Equal("b", queue.CurrentItem!.Track.Title);
        queue.Remove(queue.CurrentItem.Id);
        Assert.Equal(2, queue.Count);
        queue.SetSingle(T("z"));
        queue.Restore(snapshot);
        Assert.Equal(["a", "b", "c"], Titles(queue));
        Assert.Equal("b", queue.CurrentItem!.Track.Title);
    }
}
