using System.Text.Json;

namespace Melogold.Playback;

/// <summary>
/// Кэш музыки (tasks/0003): всё, что играет, пишется кусками по мере чтения; повтор идёт с диска, а трек, прочитанный
/// целиком, играет без сети и без единого запроса (ни InnerTube <c>player</c>, ни адреса). Ключ — <c>videoId</c>: у трека
/// один формат, при смене формата старые байты удаляются. Сверх лимита уходят треки, которые дольше всех не слушали;
/// играющий не трогается.
/// <para>
/// На трек — <c>&lt;videoId&gt;.&lt;itag&gt;.data</c> (байты по своим смещениям) и <c>.json</c> (сведения о потоке без адреса,
/// длина, прочитанные диапазоны). Индекс (формат, длина, сколько байт есть, когда читали) — в памяти: читается с диска
/// один раз, дальше вытеснение обходится без диска.
/// </para>
/// </summary>
public sealed class SongCache(string directory, Func<long> maxBytes, Action<string, Exception?> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private Dictionary<string, SongCacheEntry>? _index;

    public string Directory => directory;

    /// <summary>Трек стал целиком в кэше или ушёл из него: метки «есть без сети» обновляются.</summary>
    public event Action<string>? Changed;

    internal static JsonSerializerOptions Options => Json;

    internal Action<string, Exception?> Log => log;

    private Dictionary<string, SongCacheEntry> Index
    {
        get
        {
            lock (_lock)
            {
                if (_index is not null) return _index;
                _index = new Dictionary<string, SongCacheEntry>(StringComparer.Ordinal);
                if (System.IO.Directory.Exists(directory))
                {
                    foreach (var meta in new DirectoryInfo(directory).EnumerateFiles("*.json"))
                    {
                        if (SongCacheEntry.Read(this, Path.ChangeExtension(meta.FullName, null), meta.LastWriteTimeUtc) is { } entry)
                        {
                            // Два формата одного трека (до этой версии): остаётся тот, что читали позже
                            if (_index.TryGetValue(entry.VideoId, out var other) && other.LastRead >= entry.LastRead) entry.DeleteFiles();
                            else
                            {
                                other?.DeleteFiles();
                                _index[entry.VideoId] = entry;
                            }
                        }
                    }
                }
                return _index;
            }
        }
    }

    /// <summary>Занято на диске, байт (по индексу).</summary>
    public long Size
    {
        get
        {
            lock (_lock) return Index.Values.Sum(e => e.DiskBytes);
        }
    }

    /// <summary>Запись для потока и отметка «играет»: пока её не отпустят (<see cref="SongCacheEntry.Release"/>), не вытесняется.</summary>
    public SongCacheEntry Entry(StreamInfo info)
    {
        SongCacheEntry entry;
        lock (_lock)
        {
            if (Index.TryGetValue(info.VideoId, out var known) && known.Itag == info.Itag && (info.ContentLength is null || known.Total is null || known.Total == info.ContentLength))
                entry = known;
            else
            {
                // Другой формат или другая длина: старые байты трека уходят
                known?.DeleteFiles();
                entry = SongCacheEntry.Create(this, Path.Combine(directory, $"{info.VideoId}.{info.Itag}"), info);
                Index[info.VideoId] = entry;
            }
            entry.Pin();
        }
        entry.Touch(persist: true);
        Trim();
        return entry;
    }

    /// <summary>Трек целиком в кэше: сведения о потоке для воспроизведения без сети; null — нет.</summary>
    public StreamInfo? Complete(string videoId)
    {
        lock (_lock) return Index.TryGetValue(videoId, out var entry) && entry.IsComplete ? entry.OfflineInfo : null;
    }

    /// <summary>Есть без сети: трек целиком в кэше (метка в строках, без запроса к диску).</summary>
    public bool IsComplete(string videoId)
    {
        lock (_lock) return Index.TryGetValue(videoId, out var entry) && entry.IsComplete;
    }

    /// <summary>Треки целиком в кэше, сначала недавно слушанные (для «Скачанного» › «В кэше»).</summary>
    public List<(string VideoId, long Bytes)> CompleteTracks()
    {
        lock (_lock) return Index.Values.Where(e => e.IsComplete).OrderByDescending(e => e.LastRead).Select(e => (e.VideoId, e.DiskBytes)).ToList();
    }

    /// <summary>Сверх лимита — удалить треки, которые дольше всех не слушали; играющие остаются.</summary>
    public void Trim()
    {
        var max = maxBytes();
        if (max <= 0) return;
        var removed = new List<string>();
        lock (_lock)
        {
            var size = Index.Values.Sum(e => e.DiskBytes);
            foreach (var entry in Index.Values.OrderBy(e => e.LastRead).ToList())
            {
                if (size <= max) break;
                if (entry.Pinned) continue;
                size -= entry.DiskBytes;
                entry.DeleteFiles();
                Index.Remove(entry.VideoId);
                removed.Add(entry.VideoId);
            }
        }
        foreach (var videoId in removed) Changed?.Invoke(videoId);
    }

    /// <summary>«Очистить кэш»: всё, кроме играющего сейчас.</summary>
    public void Clear()
    {
        var removed = new List<string>();
        lock (_lock)
        {
            foreach (var entry in Index.Values.Where(e => !e.Pinned).ToList())
            {
                entry.DeleteFiles();
                Index.Remove(entry.VideoId);
                removed.Add(entry.VideoId);
            }
        }
        foreach (var videoId in removed) Changed?.Invoke(videoId);
    }

    /// <summary>
    /// Трек, целиком лежащий в <paramref name="source"/>, — копией сюда (загрузка трека из кэша: сразу и без сети).
    /// false — там его нет целиком или копия не удалась.
    /// </summary>
    public bool CopyFrom(SongCache source, string videoId)
    {
        SongCacheEntry? from;
        lock (source._lock) from = source.Index.TryGetValue(videoId, out var known) && known.IsComplete ? known : null;
        if (from is null) return false;
        var basePath = Path.Combine(directory, $"{videoId}.{from.Itag}");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            if (!from.CopyTo(basePath)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Song cache: {videoId} not copied", e);
            return false;
        }
        if (SongCacheEntry.Read(this, basePath, DateTime.UtcNow) is not { IsComplete: true } entry) return false;
        lock (_lock)
        {
            if (Index.TryGetValue(videoId, out var old) && !ReferenceEquals(old, entry) && old.Itag != entry.Itag) old.DeleteFiles();
            Index[videoId] = entry;
        }
        Changed?.Invoke(videoId);
        return true;
    }

    /// <summary>
    /// Удалить трек (загрузку): байты и сведения. Если он сейчас играет, чтение уйдёт в сеть; с
    /// <paramref name="unlessPlaying"/> играющий остаётся (дубль в кэше после загрузки уйдёт при вытеснении).
    /// </summary>
    public void Remove(string videoId, bool unlessPlaying = false)
    {
        SongCacheEntry? entry;
        lock (_lock)
        {
            if (!Index.TryGetValue(videoId, out entry) || (unlessPlaying && entry.Pinned)) return;
            Index.Remove(videoId);
        }
        entry.DeleteFiles();
        Changed?.Invoke(videoId);
    }

    /// <summary>Все байты трека, если он целиком здесь (для «Сохранить файлом»); иначе null.</summary>
    public byte[]? ReadComplete(string videoId)
    {
        SongCacheEntry? entry;
        lock (_lock) entry = Index.TryGetValue(videoId, out var known) && known.IsComplete ? known : null;
        return entry is { Total: { } total } && total <= int.MaxValue && entry.TryRead(0, (int)total, out var data) ? data : null;
    }

    /// <summary>Треки, которые здесь есть (целиком или частично): загрузки, прерванные на середине, продолжаются.</summary>
    public List<string> VideoIds()
    {
        lock (_lock) return Index.Keys.ToList();
    }

    internal void OnCompleted(string videoId) => Changed?.Invoke(videoId);

    internal void Forget(SongCacheEntry entry)
    {
        lock (_lock)
        {
            if (Index.TryGetValue(entry.VideoId, out var known) && ReferenceEquals(known, entry)) Index.Remove(entry.VideoId);
        }
    }
}

/// <summary>Кэш одного трека: формат, длина, какие диапазоны байтов уже на диске, когда читали.</summary>
public sealed class SongCacheEntry
{
    private sealed record Meta(StreamInfo Info, long? Total, List<long[]> Ranges);

    private readonly SongCache _cache;
    private readonly string _basePath;
    private readonly Lock _lock = new();
    private readonly List<(long Start, long End)> _ranges = [];
    private readonly StreamInfo _info;
    private int _pins;
    private bool _deleted;

    private SongCacheEntry(SongCache cache, string basePath, StreamInfo info, long? total)
    {
        _cache = cache;
        _basePath = basePath;
        _info = info with { Url = "" };
        Total = total;
        LastRead = DateTime.UtcNow;
    }

    public string VideoId => _info.VideoId;

    public int Itag => _info.Itag;

    /// <summary>Полная длина потока (<c>contentLength</c>), когда известна.</summary>
    public long? Total { get; private set; }

    /// <summary>Когда трек читали последний раз: по этому времени — вытеснение.</summary>
    public DateTime LastRead { get; private set; }

    /// <summary>Сколько файл занимает на диске.</summary>
    public long DiskBytes { get; private set; }

    /// <summary>Играет или открыт: не вытесняется.</summary>
    public bool Pinned => Volatile.Read(ref _pins) > 0;

    private string DataPath => _basePath + ".data";

    private string MetaPath => _basePath + ".json";

    /// <summary>Целиком: есть все байты от 0 до <c>contentLength</c>, включая неполный последний кусок.</summary>
    public bool IsComplete
    {
        get
        {
            lock (_lock) return Total is { } total && _ranges.Count == 1 && _ranges[0].Start == 0 && _ranges[0].End >= total;
        }
    }

    /// <summary>Сведения о потоке для воспроизведения без адреса.</summary>
    internal StreamInfo OfflineInfo => _info with { ContentLength = Total, ExpiresAtMs = long.MaxValue };

    internal static SongCacheEntry Create(SongCache cache, string basePath, StreamInfo info)
    {
        var entry = new SongCacheEntry(cache, basePath, info, info.ContentLength);
        foreach (var file in new[] { entry.DataPath, entry.MetaPath })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
        return entry;
    }

    /// <summary>Запись из <c>.json</c> при чтении индекса; null — битая или без данных.</summary>
    internal static SongCacheEntry? Read(SongCache cache, string basePath, DateTime lastRead)
    {
        try
        {
            if (!File.Exists(basePath + ".data") || JsonSerializer.Deserialize<Meta>(File.ReadAllText(basePath + ".json"), SongCache.Options) is not { } meta) return null;
            var entry = new SongCacheEntry(cache, basePath, meta.Info, meta.Total) { LastRead = lastRead, DiskBytes = new FileInfo(basePath + ".data").Length };
            foreach (var range in meta.Ranges.Where(r => r.Length == 2)) entry.Add(range[0], range[1]);
            return entry;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            cache.Log($"Song cache {Path.GetFileName(basePath)} unreadable", e);
            return null;
        }
    }

    internal void Pin() => Interlocked.Increment(ref _pins);

    /// <summary>Трек больше не играет: его можно вытеснять.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _pins) < 0) Interlocked.Exchange(ref _pins, 0);
    }

    /// <summary>Трек слушают: в памяти сразу, на диск (время файла сведений) — при открытии.</summary>
    internal void Touch(bool persist)
    {
        LastRead = DateTime.UtcNow;
        if (!persist) return;
        try
        {
            if (File.Exists(MetaPath)) File.SetLastWriteTimeUtc(MetaPath, LastRead);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Диапазон целиком на диске — прочитать его; иначе false.</summary>
    public bool TryRead(long start, int length, out byte[] data)
    {
        data = [];
        lock (_lock)
        {
            if (_deleted || length <= 0 || !_ranges.Any(r => r.Start <= start && r.End >= start + length)) return false;
            try
            {
                using var file = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[length];
                file.ReadExactly(buffer);
                data = buffer;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _cache.Log("Song cache read failed", e);
                _ranges.Clear();
                return false;
            }
        }
        Touch(persist: false);
        return true;
    }

    /// <summary>Прочитанное из сети — сразу на диск; <paramref name="total"/> — полная длина потока, если известна.</summary>
    public void Write(long start, byte[] data, long? total)
    {
        if (data.Length == 0) return;
        bool completed;
        lock (_lock)
        {
            if (_deleted) return;
            var wasComplete = Total is { } before && _ranges.Count == 1 && _ranges[0].Start == 0 && _ranges[0].End >= before;
            try
            {
                if (total is not null) Total = total;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
                using (var file = new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    file.Seek(start, SeekOrigin.Begin);
                    file.Write(data);
                    DiskBytes = file.Length;
                }
                Add(start, start + data.Length);
                var meta = new Meta(_info, Total, _ranges.Select(r => new[] { r.Start, r.End }).ToList());
                var temp = MetaPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(meta, SongCache.Options));
                File.Move(temp, MetaPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _cache.Log("Song cache write failed", e);
                return;
            }
            completed = !wasComplete && Total is { } now && _ranges.Count == 1 && _ranges[0].Start == 0 && _ranges[0].End >= now;
        }
        LastRead = DateTime.UtcNow;
        if (completed) _cache.OnCompleted(VideoId);
    }

    /// <summary>Копия файлов записи под другим именем (<paramref name="basePath"/> без расширения).</summary>
    internal bool CopyTo(string basePath)
    {
        lock (_lock)
        {
            if (_deleted) return false;
            File.Copy(DataPath, basePath + ".data", overwrite: true);
            File.Copy(MetaPath, basePath + ".json", overwrite: true);
            return true;
        }
    }

    /// <summary>Удалить байты и сведения (вытеснение, смена формата, «Очистить кэш»).</summary>
    internal void DeleteFiles()
    {
        lock (_lock)
        {
            _deleted = true;
            _ranges.Clear();
            DiskBytes = 0;
        }
        foreach (var file in new[] { DataPath, MetaPath })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException e)
            {
                _cache.Log($"Song cache: {Path.GetFileName(file)} not deleted", e);
            }
        }
        _cache.Forget(this);
    }

    /// <summary>Добавить [start, end) и слить соседние диапазоны.</summary>
    private void Add(long start, long end)
    {
        _ranges.Add((start, end));
        _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>(_ranges.Count);
        foreach (var range in _ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        _ranges.Clear();
        _ranges.AddRange(merged);
    }
}
