using Melogold.Core.Music;

namespace Melogold.Core.Domain;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>Элемент очереди: трек и откуда он — от пользователя или из автовоспроизведения.</summary>
public sealed record QueueItem(Track Track, bool FromAutoplay, long Id);

/// <summary>Снимок очереди для «Очередь заменена · Отменить» и восстановления после перезапуска.</summary>
public sealed record QueueSnapshot(IReadOnlyList<QueueItem> Items, int Index, IReadOnlyList<int>? ShuffleOrder, long PositionMs);

/// <summary>
/// Очередь по REWRITE §4.10.4 Android, одинаковая на всех клиентах. Состав: пользовательские элементы, за ними блок
/// автовоспроизведения. «Играть следующим» — сразу после текущего; «В конец очереди» — перед первым элементом
/// автовоспроизведения; перетаскивание элемента автовоспроизведения в пользовательскую часть делает его пользовательским.
/// Перемешивание: перемешивается только пользовательская часть, текущий трек первым, блок автовоспроизведения остаётся
/// в конце; выключение возвращает исходный порядок. Повтор <c>All</c> убирает блок автовоспроизведения.
/// Чистая модель без плеера: плеер спрашивает у неё, что играть.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<QueueItem> _items = [];
    private List<int>? _shuffle;
    private long _nextId = 1;

    /// <summary>Элементы в порядке исходной очереди (без перемешивания).</summary>
    public IReadOnlyList<QueueItem> Items => _items;

    /// <summary>Индекс текущего элемента в <see cref="Items"/>; -1 — очередь пуста.</summary>
    public int Current { get; private set; } = -1;

    public RepeatMode Repeat { get; private set; } = RepeatMode.Off;

    public bool Shuffled => _shuffle is not null;

    public QueueItem? CurrentItem => Current >= 0 && Current < _items.Count ? _items[Current] : null;

    public int Count => _items.Count;

    public event Action? Changed;

    /// <summary>Порядок проигрывания: индексы <see cref="Items"/>.</summary>
    public IReadOnlyList<int> PlayOrder => _shuffle ?? Enumerable.Range(0, _items.Count).ToList();

    /// <summary>Элементы в порядке проигрывания (для панели очереди).</summary>
    public IReadOnlyList<QueueItem> Ordered => PlayOrder.Select(i => _items[i]).ToList();

    public int AutoplayStart
    {
        get
        {
            var index = _items.FindIndex(i => i.FromAutoplay);
            return index < 0 ? _items.Count : index;
        }
    }

    public int UserCount => AutoplayStart;

    /// <summary>Сколько пользовательских элементов было бы заменено: снекбар «Отменить» — при двух и больше.</summary>
    public int UserAddedCount => _items.Count(i => !i.FromAutoplay);

    private QueueItem Item(Track track, bool autoplay = false) => new(track, autoplay, _nextId++);

    // ---------- Замена ----------

    /// <summary>Играть список с выбранного трека (тап в альбоме, плейлисте, Избранном…). При перемешивании выбранный — первым.</summary>
    public void SetList(IReadOnlyList<Track> tracks, int startIndex, bool shuffle)
    {
        _items.Clear();
        _items.AddRange(tracks.Select(t => Item(t)));
        Current = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        _shuffle = null;
        if (shuffle) Shuffle(true);
        Changed?.Invoke();
    }

    /// <summary>Одиночный трек (поиск, «Недавние», ссылка): дальше — автовоспроизведение похожих.</summary>
    public void SetSingle(Track track) => SetList([track], 0, false);

    public QueueSnapshot Snapshot(long positionMs = 0) => new(_items.ToList(), Current, _shuffle?.ToList(), positionMs);

    public void Restore(QueueSnapshot snapshot)
    {
        _items.Clear();
        _items.AddRange(snapshot.Items);
        _nextId = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        Current = _items.Count == 0 ? -1 : Math.Clamp(snapshot.Index, 0, _items.Count - 1);
        _shuffle = snapshot.ShuffleOrder is { } order && order.Count == _items.Count ? order.ToList() : null;
        Changed?.Invoke();
    }

    // ---------- Вставка ----------

    /// <summary>«Играть следующим»: сразу после текущего (в порядке проигрывания).</summary>
    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (Current < 0)
        {
            SetList(tracks, 0, false);
            return;
        }
        var items = tracks.Select(t => Item(t)).ToList();
        // В исходном порядке — сразу после текущего и в пользовательской части
        var insertAt = Math.Min(Current + 1, AutoplayStart);
        if (insertAt <= Current) insertAt = Current + 1;
        _items.InsertRange(insertAt, items);
        if (_shuffle is not null)
        {
            var shifted = _shuffle.Select(i => i >= insertAt ? i + items.Count : i).ToList();
            var position = shifted.IndexOf(Current) + 1;
            shifted.InsertRange(position, Enumerable.Range(insertAt, items.Count));
            _shuffle = shifted;
        }
        Changed?.Invoke();
    }

    /// <summary>«В конец очереди»: перед блоком автовоспроизведения.</summary>
    public void AddToEnd(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (Current < 0)
        {
            SetList(tracks, 0, false);
            return;
        }
        var insertAt = AutoplayStart;
        var items = tracks.Select(t => Item(t)).ToList();
        _items.InsertRange(insertAt, items);
        if (insertAt <= Current) Current += items.Count;
        if (_shuffle is not null)
        {
            var shifted = _shuffle.Select(i => i >= insertAt ? i + items.Count : i).ToList();
            // В перемешанном порядке — перед первым элементом автовоспроизведения
            var firstAuto = shifted.FindIndex(i => i >= insertAt + items.Count);
            shifted.InsertRange(firstAuto < 0 ? shifted.Count : firstAuto, Enumerable.Range(insertAt, items.Count));
            _shuffle = shifted;
        }
        Changed?.Invoke();
    }

    /// <summary>Догрузка автовоспроизведения в конец.</summary>
    public void AppendAutoplay(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0 || Repeat == RepeatMode.All) return;
        var start = _items.Count;
        _items.AddRange(tracks.Select(t => Item(t, true)));
        _shuffle?.AddRange(Enumerable.Range(start, tracks.Count));
        if (Current < 0) Current = 0;
        Changed?.Invoke();
    }

    /// <summary>Сколько элементов автовоспроизведения осталось впереди.</summary>
    public int AutoplayAhead
    {
        get
        {
            var order = PlayOrder;
            var position = order.ToList().IndexOf(Current);
            return order.Skip(position + 1).Count(i => _items[i].FromAutoplay);
        }
    }

    // ---------- Удаление и перестановка ----------

    public void Remove(long id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || index == Current) return;
        _items.RemoveAt(index);
        if (index < Current) Current--;
        if (_shuffle is not null) _shuffle = _shuffle.Where(i => i != index).Select(i => i > index ? i - 1 : i).ToList();
        Changed?.Invoke();
    }

    /// <summary>«Очистить»: всё, кроме текущего трека.</summary>
    public void ClearExceptCurrent()
    {
        if (CurrentItem is not { } current) return;
        _items.Clear();
        _items.Add(current);
        Current = 0;
        _shuffle = _shuffle is null ? null : [0];
        Changed?.Invoke();
    }

    /// <summary>
    /// Перенести элемент на место <paramref name="toPosition"/> в порядке проигрывания. Перенос элемента
    /// автовоспроизведения в пользовательскую часть делает его пользовательским.
    /// </summary>
    public void Move(long id, int toPosition)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return;
        if (_shuffle is not null)
        {
            var from = _shuffle.IndexOf(index);
            _shuffle.RemoveAt(from);
            _shuffle.Insert(Math.Clamp(toPosition, 0, _shuffle.Count), index);
            Changed?.Invoke();
            return;
        }
        var item = _items[index];
        var currentId = CurrentItem?.Id;
        _items.RemoveAt(index);
        var target = Math.Clamp(toPosition, 0, _items.Count);
        if (item.FromAutoplay && target <= AutoplayStart) item = item with { FromAutoplay = false };
        _items.Insert(target, item);
        if (currentId is { } cid) Current = _items.FindIndex(i => i.Id == cid);
        Changed?.Invoke();
    }

    // ---------- Переходы ----------

    /// <summary>Перейти к элементу по id (клик в панели очереди).</summary>
    public bool JumpTo(long id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        Current = index;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Индекс следующего элемента в порядке проигрывания или null (конец очереди).</summary>
    public int? PeekNext(bool userAction)
    {
        if (Current < 0) return null;
        if (Repeat == RepeatMode.One && !userAction) return Current;
        var order = PlayOrder;
        var position = order.ToList().IndexOf(Current);
        if (position + 1 < order.Count) return order[position + 1];
        return Repeat == RepeatMode.All && order.Count > 0 ? order[0] : null;
    }

    public bool MoveNext(bool userAction)
    {
        if (PeekNext(userAction) is not { } next) return false;
        Current = next;
        Changed?.Invoke();
        return true;
    }

    public bool MovePrevious()
    {
        if (Current < 0) return false;
        var order = PlayOrder;
        var position = order.ToList().IndexOf(Current);
        if (position > 0) Current = order[position - 1];
        else if (Repeat == RepeatMode.All && order.Count > 1) Current = order[^1];
        else return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Индексы следующих <paramref name="count"/> элементов (для упреждающего резолва потока).</summary>
    public IReadOnlyList<int> Upcoming(int count)
    {
        var order = PlayOrder;
        var position = order.ToList().IndexOf(Current);
        return order.Skip(position + 1).Take(count).ToList();
    }

    // ---------- Режимы ----------

    public void SetRepeat(RepeatMode mode)
    {
        Repeat = mode;
        if (mode == RepeatMode.All)
        {
            // При повторе очереди автовоспроизведение не добавляется, имеющийся блок убирается
            var currentId = CurrentItem?.Id;
            if (CurrentItem is { FromAutoplay: true } playing)
            {
                var index = _items.IndexOf(playing);
                _items[index] = playing with { FromAutoplay = false };
            }
            var removed = _items.Select((item, index) => (item, index)).Where(x => x.item.FromAutoplay).Select(x => x.index).ToHashSet();
            if (removed.Count > 0)
            {
                _items.RemoveAll(i => i.FromAutoplay);
                if (_shuffle is not null)
                {
                    _shuffle = _shuffle.Where(i => !removed.Contains(i)).Select(i => i - removed.Count(r => r < i)).ToList();
                }
                Current = currentId is { } cid ? _items.FindIndex(i => i.Id == cid) : Current;
            }
        }
        Changed?.Invoke();
    }

    public void Shuffle(bool on, Random? random = null)
    {
        if (!on)
        {
            _shuffle = null;
            Changed?.Invoke();
            return;
        }
        random ??= Random.Shared;
        var user = Enumerable.Range(0, AutoplayStart).Where(i => i != Current).OrderBy(_ => random.Next()).ToList();
        var order = new List<int>();
        if (Current >= 0 && Current < AutoplayStart) order.Add(Current);
        order.AddRange(user);
        // Автовоспроизведение — в конце по порядку; если играет трек из него, он остаётся текущим
        order.AddRange(Enumerable.Range(AutoplayStart, _items.Count - AutoplayStart));
        if (Current >= AutoplayStart && Current >= 0)
        {
            order.Remove(Current);
            order.Insert(0, Current);
        }
        _shuffle = order;
        Changed?.Invoke();
    }
}
