using Melogold.Core.Data;
using Melogold.Core.Music;

namespace Melogold.InnerTube;

/// <summary>
/// Починка медиатеки (tasks/0033): клипы из альбомов, сохранённые до tasks/0032, — на песни.
///
/// До 0032 список альбома давал клипы (<c>MUSIC_VIDEO_TYPE_OMV</c>) вместо песен, и то, что человек лайкнул или положил в
/// плейлист из альбома, осталось клипом: LEMONHEAD у Tyler, The Creator играл клип на 1:25 вместо песни на 2:11. Один раз
/// после запуска для каждого альбома с такими клипами загружается альбом с песнями, и ♡ и места в плейлистах переходят
/// на песни. Синхронизация разнесёт это по остальным устройствам.
/// </summary>
public static class AlbumClipsRepair
{
    /// <summary>Отметка в <c>app_state</c>: починка прошла целиком.</summary>
    public const string DoneKey = "repair.album_clips";

    /// <summary>Не мешать запуску: окно, очередь и первая синхронизация — раньше.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Что сделано: заменено пар и нужен ли повтор (альбом не загрузился — например, нет сети; отметка не ставится,
    /// починка повторится при следующем запуске).
    /// </summary>
    public sealed record Result(int Albums, int Replaced, bool Retry);

    public static async Task<Result?> RunAsync(Library library, Func<string, CancellationToken, Task<IReadOnlyList<(string ClipId, Track Song)>>> twins,
        TimeSpan delay, Action<string, Exception?> log, CancellationToken ct = default)
    {
        if (await Task.Run(() => library.GetState(DoneKey), ct).ConfigureAwait(false) is not null) return null;
        await Task.Delay(delay, ct).ConfigureAwait(false);

        // album_id → id клипов этого альбома в медиатеке
        var albums = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var clip in await Task.Run(library.SavedAlbumClips, ct).ConfigureAwait(false))
        {
            if (clip.AlbumId is { } album) (albums.TryGetValue(album, out var set) ? set : albums[album] = []).Add(clip.VideoId);
        }

        int replaced = 0;
        var retry = false;
        foreach (var (album, clips) in albums)
        {
            IReadOnlyList<(string ClipId, Track Song)> pairs;
            try
            {
                pairs = await twins(album, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Без сети — в следующий запуск: отметка о готовности не ставится
                log($"Починка клипов: альбом {album} не загрузился", error);
                retry = true;
                continue;
            }
            foreach (var (clipId, song) in pairs.Where(pair => clips.Contains(pair.ClipId)))
            {
                try
                {
                    var what = await Task.Run(() => library.ReplaceClipWithSong(clipId, song), ct).ConfigureAwait(false);
                    replaced++;
                    log($"Клип в медиатеке заменён песней: альбом {album}, клип {clipId}, песня {song.VideoId}, ♡ {what.Liked}, плейлистов {what.Playlists}, название {what.MovedOverride}, текст {what.MovedPin}", null);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log($"Починка клипов: {clipId} не заменён", error);
                    retry = true;
                }
            }
        }

        log($"Починка клипов из альбомов: альбомов {albums.Count}, заменено {replaced}, повторить {retry}", null);
        if (!retry) await Task.Run(() => library.SetState(DoneKey, "1"), ct).ConfigureAwait(false);
        return new Result(albums.Count, replaced, retry);
    }
}
