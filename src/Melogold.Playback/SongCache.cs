using System.Text.Json;

namespace Melogold.Playback;

/// <summary>
/// Кэш песен (Android: кэш ExoPlayer и «Максимальный размер»): прочитанные диапазоны потока лежат в файле трека, и
/// повторное чтение идёт с диска. Трек, прочитанный целиком, открывается без адреса — играет и без сети. Сверх лимита
/// удаляются треки, которые дольше всех не играли.
/// <para>
/// На трек — два файла: <c>&lt;videoId&gt;.&lt;itag&gt;.data</c> (байты по своим смещениям) и <c>.json</c> (сведения о
/// потоке без адреса, длина и прочитанные диапазоны).
/// </para>
/// </summary>
public sealed class SongCache(string directory, Func<long> maxBytes, Action<string, Exception?> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Трек, который играл недавно, не удаляется: он может ещё читаться.</summary>
    private static readonly TimeSpan InUse = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SongCacheEntry> _entries = [];

    public string Directory => directory;

    /// <summary>Занято на диске, байт.</summary>
    public long Size => Files("*.data").Sum(f => f.Length);

    /// <summary>Запись для потока: прочитанное раньше остаётся, если длина та же; иначе начинается заново.</summary>
    public SongCacheEntry Entry(StreamInfo info)
    {
        var name = $"{info.VideoId}.{info.Itag}";
        SongCacheEntry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out entry!) || entry.Deleted)
            {
                entry = SongCacheEntry.Load(Path.Combine(directory, name), info, log);
                _entries[name] = entry;
            }
        }
        entry.Touch();
        Trim();
        return entry;
    }

    /// <summary>Трек, прочитанный целиком: сведения о потоке для воспроизведения без сети; null — такого нет.</summary>
    public StreamInfo? Complete(string videoId)
    {
        foreach (var meta in Files($"{videoId}.*.json"))
        {
            var info = SongCacheEntry.CompleteInfo(meta.FullName);
            if (info is not null) return info;
        }
        return null;
    }

    /// <summary>Сверх лимита — удалить треки, которые дольше всех не играли (кроме игравших последние минуты).</summary>
    public void Trim()
    {
        var max = maxBytes();
        if (max <= 0) return;
        try
        {
            var tracks = Files("*.data").Select(data => (Data: data, Used: Used(data))).OrderBy(t => t.Used).ToList();
            var size = tracks.Sum(t => t.Data.Length);
            foreach (var (data, used) in tracks)
            {
                if (size <= max) break;
                if (DateTime.UtcNow - used < InUse) continue;
                size -= data.Length;
                Delete(Path.ChangeExtension(data.FullName, null));
            }
        }
        catch (IOException e)
        {
            log("Song cache trim failed", e);
        }
    }

    public void Clear()
    {
        foreach (var data in Files("*.data")) Delete(Path.ChangeExtension(data.FullName, null));
    }

    private static DateTime Used(FileInfo data)
    {
        var meta = new FileInfo(Path.ChangeExtension(data.FullName, ".json"));
        return meta.Exists ? meta.LastWriteTimeUtc : data.LastWriteTimeUtc;
    }

    private void Delete(string basePath)
    {
        lock (_lock)
        {
            if (_entries.Remove(Path.GetFileName(basePath), out var entry)) entry.Deleted = true;
        }
        foreach (var file in new[] { basePath + ".data", basePath + ".json" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException e)
            {
                log($"Song cache: {Path.GetFileName(file)} not deleted", e);
            }
        }
    }

    private IEnumerable<FileInfo> Files(string pattern) =>
        System.IO.Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles(pattern) : [];

    internal static JsonSerializerOptions Options => Json;
}

/// <summary>Кэш одного потока: какие диапазоны байтов уже на диске.</summary>
public sealed class SongCacheEntry
{
    private sealed record Meta(StreamInfo Info, long? Total, List<long[]> Ranges);

    private readonly string _basePath;
    private readonly Action<string, Exception?> _log;
    private readonly Lock _lock = new();
    private readonly List<(long Start, long End)> _ranges = [];
    private StreamInfo _info;
    private long? _total;

    private SongCacheEntry(string basePath, StreamInfo info, Action<string, Exception?> log)
    {
        _basePath = basePath;
        _info = info with { Url = "" };
        _total = info.ContentLength;
        _log = log;
    }

    /// <summary>Запись удалена (лимит или «Очистить»): больше ничего не пишет.</summary>
    internal bool Deleted { get; set; }

    private string DataPath => _basePath + ".data";

    private string MetaPath => _basePath + ".json";

    public bool IsComplete
    {
        get
        {
            lock (_lock) return _total is { } total && _ranges.Count == 1 && _ranges[0].Start == 0 && _ranges[0].End >= total;
        }
    }

    internal static SongCacheEntry Load(string basePath, StreamInfo info, Action<string, Exception?> log)
    {
        var entry = new SongCacheEntry(basePath, info, log);
        try
        {
            if (File.Exists(entry.MetaPath) && File.Exists(entry.DataPath)
                && JsonSerializer.Deserialize<Meta>(File.ReadAllText(entry.MetaPath), SongCache.Options) is { } meta
                && (info.ContentLength is null || meta.Total is null || meta.Total == info.ContentLength))
            {
                entry._total = meta.Total ?? info.ContentLength;
                foreach (var range in meta.Ranges.Where(r => r.Length == 2)) entry.Add(range[0], range[1]);
                return entry;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log($"Song cache {Path.GetFileName(basePath)} unreadable, starts over", e);
        }
        // Нечего продолжать: другой файл потока или битые сведения
        try
        {
            File.Delete(entry.DataPath);
        }
        catch (IOException)
        {
        }
        return entry;
    }

    /// <summary>Сведения о целиком прочитанном потоке из его <c>.json</c>; null — прочитан не весь.</summary>
    internal static StreamInfo? CompleteInfo(string metaPath)
    {
        try
        {
            if (JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), SongCache.Options) is not { Total: { } total } meta) return null;
            if (!File.Exists(Path.ChangeExtension(metaPath, ".data"))) return null;
            if (meta.Ranges is not [[0, var end]] || end < total) return null;
            return meta.Info with { Url = "", ContentLength = total, ExpiresAtMs = long.MaxValue, Source = meta.Info.Source };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Трек играет: удаление по лимиту начинается с тех, что играли давно.</summary>
    internal void Touch()
    {
        try
        {
            if (File.Exists(MetaPath)) File.SetLastWriteTimeUtc(MetaPath, DateTime.UtcNow);
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
            if (Deleted || length <= 0 || !_ranges.Any(r => r.Start <= start && r.End >= start + length)) return false;
            try
            {
                using var file = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[length];
                file.ReadExactly(buffer);
                data = buffer;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log("Song cache read failed", e);
                _ranges.Clear();
                return false;
            }
        }
    }

    /// <summary>Прочитанное из сети — на диск; <paramref name="total"/> — полная длина потока, если известна.</summary>
    public void Write(long start, byte[] data, long? total)
    {
        if (data.Length == 0) return;
        lock (_lock)
        {
            if (Deleted) return;
            try
            {
                if (total is not null) _total = total;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
                using (var file = new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    file.Seek(start, SeekOrigin.Begin);
                    file.Write(data);
                }
                Add(start, start + data.Length);
                var meta = new Meta(_info, _total, _ranges.Select(r => new[] { r.Start, r.End }).ToList());
                var temp = MetaPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(meta, SongCache.Options));
                File.Move(temp, MetaPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log("Song cache write failed", e);
            }
        }
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
