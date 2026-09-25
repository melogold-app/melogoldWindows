using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Core.Tests;

public class PlaylistDiffTests
{
    [Fact]
    public void MovingOneTrackIsOneOp()
    {
        var changes = PlaylistDiff.Changes(["a", "b", "c", "d"], ["a", "c", "d", "b"]);
        var move = Assert.IsType<ItemChange.Move>(Assert.Single(changes));
        Assert.Equal("b", move.VideoId);
        Assert.Equal("d", move.After);
    }

    [Fact]
    public void NewTracksAtTheStartGoBeforeTheFirstStaying()
    {
        var changes = PlaylistDiff.Changes(["a", "b"], ["x", "y", "a", "b"]);
        var add = Assert.IsType<ItemChange.Add>(Assert.Single(changes));
        Assert.Equal(["x", "y"], add.VideoIds);
        Assert.Null(add.After);
        Assert.Equal("a", add.Before);
    }

    /// <summary>Как <c>PlaylistDiffTest</c> Android: случайные правки, применённые по правилам якорей, дают новый список.</summary>
    [Fact]
    public void RandomEditsRoundTrip()
    {
        var random = new Random(20260925);
        for (var round = 0; round < 2000; round++)
        {
            var pool = Enumerable.Range(0, 30).Select(i => $"v{i:00}").ToList();
            var before = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 15)).ToList();
            var after = before.ToList();
            var edits = random.Next(0, 6);
            for (var e = 0; e < edits; e++)
            {
                switch (random.Next(3))
                {
                    case 0 when after.Count > 0:
                        after.RemoveAt(random.Next(after.Count));
                        break;
                    case 1:
                        var fresh = pool.Where(p => !after.Contains(p)).ToList();
                        if (fresh.Count > 0) after.Insert(random.Next(after.Count + 1), fresh[random.Next(fresh.Count)]);
                        break;
                    case 2 when after.Count > 1:
                        var index = random.Next(after.Count);
                        var item = after[index];
                        after.RemoveAt(index);
                        after.Insert(random.Next(after.Count + 1), item);
                        break;
                }
            }
            var result = PlaylistDiff.Apply(before, PlaylistDiff.Changes(before, after));
            Assert.Equal(after, result);
        }
    }
}
