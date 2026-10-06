using System.Diagnostics;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Поток трека целиком, как его читает плеер (фрагменты диапазонами, свежий адрес на 403): трек не должен обрываться
/// посередине. Пользователь 2026-10-06: вторая песня альбома Jin «Kagerou Days» обрывалась на половине, в журнале —
/// «googlevideo 403 after fresh URLs». Только с <c>MELOGOLD_LIVE=1</c>; один запрос <c>player</c> на трек.
/// </summary>
public class LiveStreamReadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WholeTrackReadsToTheEnd()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var client = new InnerTubeClient();
        var music = new YouTubeMusic(client);
        var query = Environment.GetEnvironmentVariable("MELOGOLD_ALBUM") ?? "Jin Kagerou Days";
        var albums = await music.SearchAsync(query, MusicSearchFilter.Albums, TestContext.Current.CancellationToken);
        var album = albums.Items.OfType<AlbumItem>().First();
        var page = await music.AlbumAsync(album.BrowseId, TestContext.Current.CancellationToken);
        var index = int.TryParse(Environment.GetEnvironmentVariable("MELOGOLD_TRACK_INDEX"), out var i) ? i : 1;
        var track = page.Tracks[index];
        output.WriteLine($"{album.Title} ({album.BrowseId}) — {index + 1}. {track.Title} [{track.VideoId}] {track.DurationText}");

        var lines = new List<string>();
        var resolver = new StreamResolver(client, lines.Add);
        // MELOGOLD_CLIENT=ANDROID_VR — проверить запасной клиент
        if (Environment.GetEnvironmentVariable("MELOGOLD_CLIENT") is { Length: > 0 } name)
            resolver.Clients = [new[] { ClientProfile.VisionOs, ClientProfile.AndroidVr }.First(c => c.Name == name)];
        var refreshes = 0;
        var info = await resolver.ResolveAsync(track.VideoId, TestContext.Current.CancellationToken, probe: true);
        output.WriteLine($"поток: {info.Source} itag {info.Itag}, {info.ContentLength} байт, {info.MimeType}");
        using var http = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true })
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var reader = new HttpRangeReader(http, info, async ct =>
        {
            refreshes++;
            output.WriteLine($"  свежий адрес #{refreshes}");
            resolver.Invalidate(track.VideoId);
            return await resolver.ResolveAsync(track.VideoId, ct);
        });
        long position = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var bytes = await reader.ReadAsync(position, 512 * 1024, TestContext.Current.CancellationToken);
                if (bytes.Length == 0) break;
                position += bytes.Length;
                if (reader.TotalLength is { } total && position >= total) break;
            }
        }
        catch (StreamException e)
        {
            output.WriteLine($"✗ оборвалось на {position} из {reader.TotalLength} ({100.0 * position / (reader.TotalLength ?? 1):F0} %): {e.Kind} {e.Message}");
            foreach (var line in lines) output.WriteLine("  " + line);
            throw;
        }
        output.WriteLine($"✓ прочитан целиком: {position} байт за {watch.Elapsed.TotalSeconds:F1} с, свежих адресов {refreshes}");
    }
}
