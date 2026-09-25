namespace Melogold.Core.Domain;

/// <summary>Одно изменение треков плейлиста в терминах ops сервера (API §4.8).</summary>
public abstract record ItemChange
{
    public sealed record Remove(string VideoId) : ItemChange;

    /// <summary>Новые треки одним блоком: сразу после <paramref name="After"/>, иначе перед <paramref name="Before"/>, иначе в конец.</summary>
    public sealed record Add(IReadOnlyList<string> VideoIds, string? After, string? Before) : ItemChange;

    /// <summary>Трек сменил место: сразу после <paramref name="After"/>, иначе перед <paramref name="Before"/>, иначе в конец.</summary>
    public sealed record Move(string VideoId, string? After, string? Before) : ItemChange;
}

/// <summary>
/// Ops, которые превращают плейлист, каким его знал сервер (<c>before</c>, в его порядке), в плейлист этого устройства
/// (<c>after</c>) — вариант синхронизации со снимком (REWRITE §4.12a Android, <c>PlaylistDiff.kt</c>): сначала убранные
/// треки, затем в новом порядке новые треки блоками и перемещённые, каждый — сразу после соседа в новом порядке
/// (у стоящих в начале — перед первым оставшимся). Треки самой длинной цепочки, сохранившей порядок, остаются на местах,
/// поэтому перенос одного трека — одна op. Отправляется только то, что изменило это устройство.
/// </summary>
public static class PlaylistDiff
{
    /// <summary>Не больше стольких треков в одном <c>playlist.items.add</c> (API §4.8).</summary>
    public const int MaxAdd = 500;

    public static List<ItemChange> Changes(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changes = new List<ItemChange>();
        var afterSet = after.ToHashSet(StringComparer.Ordinal);
        var beforeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < before.Count; i++) beforeIndex.TryAdd(before[i], i);

        foreach (var videoId in beforeIndex.OrderBy(pair => pair.Value).Select(pair => pair.Key))
        {
            if (!afterSet.Contains(videoId)) changes.Add(new ItemChange.Remove(videoId));
        }

        var kept = after.Where(beforeIndex.ContainsKey).ToList();
        var staying = LongestIncreasingRun(kept.Select(v => beforeIndex[v]).ToList()).Select(i => kept[i]).ToHashSet(StringComparer.Ordinal);
        var firstStaying = after.FirstOrDefault(staying.Contains);

        string? previous = null;
        var block = new List<string>();
        string? blockAfter = null;

        void Flush()
        {
            var anchor = blockAfter;
            foreach (var chunk in block.Chunk(MaxAdd))
            {
                changes.Add(new ItemChange.Add(chunk, anchor, anchor is null ? firstStaying : null));
                anchor = chunk[^1];
            }
            block.Clear();
        }

        foreach (var videoId in after)
        {
            if (!beforeIndex.ContainsKey(videoId))
            {
                if (block.Count == 0) blockAfter = previous;
                block.Add(videoId);
            }
            else
            {
                Flush();
                if (!staying.Contains(videoId))
                    changes.Add(new ItemChange.Move(videoId, previous, previous is null ? firstStaying : null));
            }
            previous = videoId;
        }
        Flush();
        return changes;
    }

    /// <summary>Индексы одной самой длинной строго возрастающей подпоследовательности (терпеливая сортировка, O(n log n)).</summary>
    internal static List<int> LongestIncreasingRun(IReadOnlyList<int> values)
    {
        if (values.Count == 0) return [];
        var tails = new int[values.Count];
        var parent = new int[values.Count];
        Array.Fill(parent, -1);
        var length = 0;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            int low = 0, high = length;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (values[tails[middle]] < value) low = middle + 1;
                else high = middle;
            }
            if (low > 0) parent[index] = tails[low - 1];
            tails[low] = index;
            if (low == length) length++;
        }
        var run = new int[length];
        var current = tails[length - 1];
        for (var position = length - 1; position >= 0; position--)
        {
            run[position] = current;
            current = parent[current];
        }
        return [.. run];
    }

    /// <summary>
    /// Применяет изменения к списку по правилам якорей (DESIGN §3.7): <c>after</c> в списке — сразу после него, иначе
    /// <c>before</c> — сразу перед ним, иначе в конец. Для проверки: <c>Apply(before, Changes(before, after)) == after</c>.
    /// </summary>
    public static List<string> Apply(IReadOnlyList<string> list, IEnumerable<ItemChange> changes)
    {
        var result = list.ToList();
        foreach (var change in changes)
        {
            switch (change)
            {
                case ItemChange.Remove remove:
                    result.Remove(remove.VideoId);
                    break;
                case ItemChange.Add add:
                {
                    var fresh = add.VideoIds.Distinct(StringComparer.Ordinal).Where(v => !result.Contains(v)).ToList();
                    result.InsertRange(Position(result, add.After, add.Before), fresh);
                    break;
                }
                case ItemChange.Move move:
                {
                    if (!result.Remove(move.VideoId)) break;
                    result.Insert(Position(result, move.After, move.Before), move.VideoId);
                    break;
                }
            }
        }
        return result;
    }

    private static int Position(List<string> list, string? after, string? before)
    {
        if (after is not null)
        {
            var index = list.IndexOf(after);
            if (index >= 0) return index + 1;
        }
        if (before is not null)
        {
            var index = list.IndexOf(before);
            if (index >= 0) return index;
        }
        return list.Count;
    }
}
