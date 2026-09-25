using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>Кэш песен: прочитанные диапазоны на диске, трек целиком — без сети, лимит по давности.</summary>
public sealed class SongCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-songs-{Guid.NewGuid():N}");
    private long _max;

    private SongCache Cache() => new(_directory, () => _max, (_, _) => { });

    private static StreamInfo Info(string id = "dQw4w9WgXcQ", long length = 1000) => new()
    {
        VideoId = id,
        Url = "https://example.invalid/stream",
        Itag = 140,
        ContentLength = length,
        Source = "VISIONOS",
        LoudnessDb = -7.5,
    };

    private static byte[] Bytes(int start, int length) => Enumerable.Range(start, length).Select(i => (byte)(i % 251)).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ReadsOnlyWhatIsOnDisk()
    {
        var entry = Cache().Entry(Info());
        Assert.False(entry.TryRead(0, 100, out _));
        entry.Write(0, Bytes(0, 300), 1000);
        entry.Write(500, Bytes(500, 100), 1000);
        Assert.True(entry.TryRead(100, 200, out var data));
        Assert.Equal(Bytes(100, 200), data);
        // Дыра 300–500: частично на диске — не считается
        Assert.False(entry.TryRead(200, 400, out _));
        Assert.False(entry.IsComplete);
    }

    [Fact]
    public void WholeTrackPlaysWithoutAddress()
    {
        var cache = Cache();
        var entry = cache.Entry(Info());
        entry.Write(0, Bytes(0, 600), 1000);
        Assert.Null(cache.Complete("dQw4w9WgXcQ"));
        entry.Write(600, Bytes(600, 400), 1000);
        Assert.True(entry.IsComplete);

        // Другой запуск: сведения о потоке — из кэша, адреса нет
        var info = Cache().Complete("dQw4w9WgXcQ");
        Assert.NotNull(info);
        Assert.Equal("", info.Url);
        Assert.Equal(-7.5, info.LoudnessDb);
        Assert.Equal(1000, info.ContentLength);
        Assert.True(Cache().Entry(info).TryRead(0, 1000, out var all));
        Assert.Equal(Bytes(0, 1000), all);
    }

    [Fact]
    public void OtherLengthStartsOver()
    {
        Cache().Entry(Info()).Write(0, Bytes(0, 1000), 1000);
        var entry = Cache().Entry(Info(length: 2000));
        Assert.False(entry.TryRead(0, 10, out _));
    }

    [Fact]
    public void TrimsLongestUnusedFirst()
    {
        var cache = Cache();
        cache.Entry(Info("aaaaaaaaaaa", 4000)).Write(0, new byte[4000], 4000);
        cache.Entry(Info("bbbbbbbbbbb", 4000)).Write(0, new byte[4000], 4000);
        // «a» играл давно, «b» — только что
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "aaaaaaaaaaa.140.json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "bbbbbbbbbbb.140.json"), DateTime.UtcNow.AddDays(-1));
        _max = 5000;
        cache.Trim();
        Assert.Null(cache.Complete("aaaaaaaaaaa"));
        Assert.NotNull(cache.Complete("bbbbbbbbbbb"));
        Assert.Equal(4000, cache.Size);

        // Без ограничения ничего не удаляется
        _max = 0;
        cache.Trim();
        Assert.Equal(4000, cache.Size);
    }
}
