using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>Кэш музыки (tasks/0003 §5): индекс, вытеснение по давности, играющий не трогается, «целиком», смена формата.</summary>
public sealed class SongCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-songs-{Guid.NewGuid():N}");
    private long _max;

    private SongCache Cache() => new(_directory, () => _max, (_, _) => { });

    private static StreamInfo Info(string id = "dQw4w9WgXcQ", long length = 1000, int itag = 140) => new()
    {
        VideoId = id,
        Url = "https://example.invalid/stream",
        Itag = itag,
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
    public void WholeTrackIncludingAShortLastChunkPlaysWithoutAddress()
    {
        var cache = Cache();
        var changed = new List<string>();
        cache.Changed += changed.Add;
        var entry = cache.Entry(Info(length: 1000));
        entry.Write(0, Bytes(0, 600), 1000);
        Assert.Null(cache.Complete("dQw4w9WgXcQ"));
        // Последний кусок короче остальных
        entry.Write(600, Bytes(600, 400), 1000);
        Assert.True(entry.IsComplete);
        Assert.True(cache.IsComplete("dQw4w9WgXcQ"));
        Assert.Equal(["dQw4w9WgXcQ"], changed);

        // Другой запуск: индекс читается с диска, сведения о потоке — без адреса
        var again = Cache();
        var info = again.Complete("dQw4w9WgXcQ");
        Assert.NotNull(info);
        Assert.Equal("", info.Url);
        Assert.Equal(-7.5, info.LoudnessDb);
        Assert.Equal(1000, info.ContentLength);
        Assert.True(again.Entry(info).TryRead(0, 1000, out var all));
        Assert.Equal(Bytes(0, 1000), all);
        Assert.Single(again.CompleteTracks());
    }

    [Fact]
    public void AnotherFormatDropsTheOldBytes()
    {
        var cache = Cache();
        cache.Entry(Info(itag: 140)).Write(0, Bytes(0, 1000), 1000);
        Assert.True(cache.IsComplete("dQw4w9WgXcQ"));
        var opus = cache.Entry(Info(itag: 251, length: 800));
        Assert.False(cache.IsComplete("dQw4w9WgXcQ"));
        Assert.False(opus.TryRead(0, 10, out _));
        Assert.Empty(Directory.GetFiles(_directory, "*.140.*"));
    }

    [Fact]
    public void LongestUnlistenedGoesFirstAndThePlayingOneStays()
    {
        var cache = Cache();
        var old = cache.Entry(Info("aaaaaaaaaaa", 4000));
        old.Write(0, new byte[4000], 4000);
        old.Release();
        Thread.Sleep(20);
        var recent = cache.Entry(Info("bbbbbbbbbbb", 4000));
        recent.Write(0, new byte[4000], 4000);
        recent.Release();
        Thread.Sleep(20);
        // Играет сейчас: закреплён, хотя и не новее
        var playing = cache.Entry(Info("ccccccccccc", 4000));
        playing.Write(0, new byte[4000], 4000);

        _max = 9000;
        cache.Trim();
        Assert.False(cache.IsComplete("aaaaaaaaaaa"));
        Assert.True(cache.IsComplete("bbbbbbbbbbb"));
        Assert.True(cache.IsComplete("ccccccccccc"));
        Assert.Equal(8000, cache.Size);

        // Лимит 32 МБ-подобный — меньше одного трека: уходят все, кроме играющего
        _max = 1000;
        cache.Trim();
        Assert.Equal(["ccccccccccc"], cache.CompleteTracks().Select(t => t.VideoId));

        // Без ограничения ничего не удаляется
        _max = 0;
        playing.Release();
        cache.Trim();
        Assert.Equal(4000, cache.Size);
    }

    [Fact]
    public void DownloadCopiesAWholeTrackFromTheCacheAndSurvivesClearing()
    {
        var songs = Cache();
        var entry = songs.Entry(Info(length: 1000));
        entry.Write(0, Bytes(0, 1000), 1000);
        entry.Release();
        var downloads = new SongCache(Path.Combine(_directory, "downloads"), () => 0, (_, _) => { });

        // Неполный трек не копируется
        Assert.False(downloads.CopyFrom(songs, "missing0000"));
        Assert.True(downloads.CopyFrom(songs, "dQw4w9WgXcQ"));
        Assert.True(downloads.IsComplete("dQw4w9WgXcQ"));
        Assert.Equal(Bytes(0, 1000), downloads.ReadComplete("dQw4w9WgXcQ"));

        // «Очистить кэш» загрузок не касается; удалённая загрузка уходит с диска
        songs.Clear();
        Assert.False(songs.IsComplete("dQw4w9WgXcQ"));
        Assert.True(new SongCache(Path.Combine(_directory, "downloads"), () => 0, (_, _) => { }).IsComplete("dQw4w9WgXcQ"));
        downloads.Remove("dQw4w9WgXcQ");
        Assert.False(downloads.IsComplete("dQw4w9WgXcQ"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "downloads")));
    }
}
