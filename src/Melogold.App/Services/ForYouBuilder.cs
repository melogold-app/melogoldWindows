using System.Text.Json;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;

namespace Melogold.App.Services;

/// <summary>Подборка «Для вас»: треки, альбомы, исполнители и плейлисты.</summary>
public sealed record ForYou(long CreatedAt, List<Track> Tracks, List<AlbumItem> Albums, List<ArtistItem> Artists, List<PlaylistItem> Playlists)
{
    public bool IsEmpty => Tracks.Count == 0 && Albums.Count == 0 && Artists.Count == 0 && Playlists.Count == 0;
}

/// <summary>
/// «Для вас» (REWRITE §4.10.5 Android): до трёх затравок — последний лайк, самый частый трек за 30 дней, последний
/// прослушанный; по каждой — «Похожие» YouTube Music; слияние по кругу без повторов и без скрытых треков: 20 треков и по 10
/// альбомов, исполнителей и плейлистов. Кэш в файле на 6 часов. Без затравок (новичок) подборки нет — экран предлагает
/// рекомендации главной YouTube Music.
/// </summary>
public sealed class ForYouBuilder(YouTubeMusic music, Library library)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string CachePath => Path.Combine(AppPaths.Cache, "foryou.json");

    public async Task<ForYou?> GetAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && Load() is { } cached && Core.Domain.IsoTime.NowMs() - cached.CreatedAt < 6 * 3600_000) return cached;
        var seeds = await Task.Run(library.ForYouSeeds, ct);
        if (seeds.Count == 0) return null;

        var perSeed = new List<List<Shelf>>();
        foreach (var seed in seeds)
        {
            try
            {
                var next = await music.NextAsync(seed.VideoId, ct: ct);
                if (next.RelatedBrowseId is { } related) perSeed.Add(await music.RelatedAsync(related, ct));
            }
            catch (YouTubeException e)
            {
                Log.Warn("For you seed failed", e);
            }
        }
        if (perSeed.Count == 0) return Load();

        var hidden = library.HiddenTracks();
        var known = seeds.Select(s => s.VideoId).ToHashSet();
        var tracks = RoundRobin(perSeed.Select(s => s.SelectMany(x => x.Items).OfType<Track>()), t => t.VideoId, 20, t => !hidden.Contains(t.VideoId) && !known.Contains(t.VideoId));
        var albums = RoundRobin(perSeed.Select(s => s.SelectMany(x => x.Items).OfType<AlbumItem>()), a => a.BrowseId, 10);
        var artists = RoundRobin(perSeed.Select(s => s.SelectMany(x => x.Items).OfType<ArtistItem>()), a => a.BrowseId, 10);
        var playlists = RoundRobin(perSeed.Select(s => s.SelectMany(x => x.Items).OfType<PlaylistItem>()), p => p.PlaylistId, 10);
        var result = new ForYou(Core.Domain.IsoTime.NowMs(), tracks, albums, artists, playlists);
        Save(result);
        return result;
    }

    private static List<T> RoundRobin<T>(IEnumerable<IEnumerable<T>> sources, Func<T, string> key, int max, Func<T, bool>? allow = null)
    {
        var queues = sources.Select(s => new Queue<T>(s)).ToList();
        var seen = new HashSet<string>();
        var result = new List<T>();
        while (result.Count < max && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if ((allow?.Invoke(item) ?? true) && seen.Add(key(item)))
                    {
                        result.Add(item);
                        break;
                    }
                }
                if (result.Count >= max) break;
            }
        }
        return result;
    }

    private static ForYou? Load()
    {
        try
        {
            return File.Exists(CachePath) ? JsonSerializer.Deserialize<ForYou>(File.ReadAllText(CachePath), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private static void Save(ForYou value)
    {
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(value, Json));
        }
        catch (IOException e)
        {
            Log.Warn("For you not cached", e);
        }
    }
}
