using Melogold.Core.Lyrics;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.InnerTube.Lyrics;
using Xunit;

namespace Melogold.Tests;

/// <summary>Цепочка текстов на живых источниках (§8.2): песня YouTube Music и видео YouTube. Только с <c>MELOGOLD_LIVE=1</c>.</summary>
public class LiveLyricsTests(ITestOutputHelper output)
{
    private static LyricsFetcher Fetcher(out YouTubeMusic music)
    {
        music = new YouTubeMusic(new InnerTubeClient());
        return new LyricsFetcher(music, new LrcLib(LrcLib.CreateClient("Melogold-test/0.1 (+https://github.com/melogold-app/melogoldWindows)")), new KuGou(KuGou.CreateClient()));
    }

    [Fact]
    public async Task SongGetsSyncedLyrics()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var fetcher = Fetcher(out var music);
        var song = (await music.SearchAsync("Кино Группа крови", MusicSearchFilter.Songs)).Items.OfType<Track>().First();
        var result = await fetcher.FetchAsync(song, song.DurationMs ?? 285_000, null);
        output.WriteLine($"{song.Title} · {song.ArtistsText}: synced={result.SyncedSource} plain={result.PlainSource} failure={result.AnyFailure}");
        Assert.NotNull(result.Synced);
        var lyrics = LyricsFormats.ParseSynced(result.Synced!);
        Assert.NotNull(lyrics);
        Assert.True(lyrics.Lines.Count > 10);
        output.WriteLine(string.Join("\n", lyrics.Lines.Take(3).Select(l => $"{l.StartMs} {l.Text}")));
    }

    [Fact]
    public async Task VideoGetsSyncedLyricsFromLrcLib()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var fetcher = Fetcher(out _);
        var video = new Track { VideoId = "dQw4w9WgXcQ", Title = "Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", ArtistsText = "Rick Astley", DurationMs = 213_000, VideoType = "video" };
        var result = await fetcher.FetchAsync(video, 213_000, null);
        output.WriteLine($"synced={result.SyncedSource} plain={result.PlainSource} failure={result.AnyFailure}");
        Assert.NotNull(result.Synced);
        Assert.NotNull(LyricsFormats.ParseSynced(result.Synced!));
    }
}
