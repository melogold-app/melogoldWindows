using Melogold.Core.Domain;
using Melogold.Core.Music;

namespace Melogold.Server;

/// <summary>Трек и схемы API (§4.1): <c>TrackInput</c> в запросах, <c>TrackDto</c> в ответах.</summary>
public static class TrackDtos
{
    public static TrackInput Input(Track track) => new()
    {
        VideoId = track.VideoId,
        // Заглушка (название = videoId) — без названия: сервер оставит своё
        Title = track.Title == track.VideoId ? null : track.Title,
        ArtistsText = track.ArtistsText,
        Artists = track.Artists.Count > 0 ? track.Artists.Select(a => new ArtistRefDto(a.Id, a.Name)).ToList() : null,
        AlbumId = track.AlbumId,
        AlbumTitle = track.AlbumTitle,
        DurationMs = track.DurationMs,
        DurationText = track.DurationText,
        ThumbnailUrl = track.ThumbnailUrl,
        Explicit = track.Explicit ? true : null,
        VideoType = track.VideoType,
    };

    /// <summary>Метаданные трека с сервера; у заглушки их нет.</summary>
    public static Track? ToTrack(TrackDto dto) => dto.MetadataStub || string.IsNullOrWhiteSpace(dto.Title) ? null : new Track
    {
        VideoId = dto.VideoId,
        Title = dto.Title,
        ArtistsText = dto.ArtistsText,
        Artists = dto.Artists?.Select(a => new ArtistRef(a.Id, a.Name)).ToList() ?? [],
        AlbumId = dto.AlbumId,
        AlbumTitle = dto.AlbumTitle,
        DurationMs = dto.DurationMs,
        DurationText = dto.DurationText,
        ThumbnailUrl = dto.ThumbnailUrl,
        Explicit = dto.Explicit,
        VideoType = dto.VideoType,
    };

    /// <summary>Трек снимка: у заглушки название — её videoId, трек всё равно играет.</summary>
    public static Track ToTrackOrStub(TrackDto dto) => ToTrack(dto) ?? new Track { VideoId = dto.VideoId, Title = dto.VideoId };
}

/// <summary>Чем кончилось «Поделиться» своим плейлистом (tasks/0016).</summary>
public abstract record PlaylistShare
{
    /// <summary>Снимок на сервере аккаунта: ссылка открывается в приложении и в браузере у кого угодно.</summary>
    public sealed record OnServer(string Url, string ShareId) : PlaylistShare;

    /// <summary>Сервера нет: первые <paramref name="Shown"/> из <paramref name="Total"/> видео одним списком YouTube.</summary>
    public sealed record OnYouTube(string Url, int Shown, int Total) : PlaylistShare;

    /// <summary>Ни одного видео YouTube — делиться нечем.</summary>
    public sealed record NoTracks : PlaylistShare;

    /// <summary>У аккаунта столько ссылок, сколько держит сервер (<c>409 share_limit_reached</c>): сначала удалить старые.</summary>
    public sealed record LimitReached(int Max) : PlaylistShare;
}

/// <summary>
/// Ссылка на свой плейлист (API §4.11, tasks/0016): снимок на сервере аккаунта, если он умеет (<c>features.share</c>);
/// без входа, без сервера или при его сбое — первые 50 видео списком YouTube, его откроет любой.
/// </summary>
public sealed class PlaylistSharing(AccountService account)
{
    public const int MaxTracks = 1000;
    private const int MaxName = 200;
    private const int DefaultMaxShares = 200;

    /// <param name="tracks">треки по порядку, уже со своими названиями (tasks/0011)</param>
    public async Task<PlaylistShare> ShareAsync(string name, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        var shareable = tracks.Where(t => ShareLinks.IsVideoId(t.VideoId)).Take(MaxTracks).ToList();
        if (shareable.Count == 0) return new PlaylistShare.NoTracks();
        if (await account.SharesAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var created = await account.CreateShareAsync(Utf16.Truncate(name.Trim(), MaxName), shareable.Select(TrackDtos.Input).ToList(), ct).ConfigureAwait(false);
                return new PlaylistShare.OnServer(created.Url, created.ShareId);
            }
            catch (ApiException e) when (e.Code == "share_limit_reached")
            {
                return new PlaylistShare.LimitReached(e.Envelope?.MaxShares ?? DefaultMaxShares);
            }
            catch (ApiException)
            {
                // Нет сети, сервер занят: список YouTube всё равно откроется
            }
        }
        var ids = shareable.Select(t => t.VideoId).ToList();
        return ShareLinks.WatchVideos(ids) is { } url
            ? new PlaylistShare.OnYouTube(url, Math.Min(ids.Count, ShareLinks.WatchVideosLimit), ids.Count)
            : new PlaylistShare.NoTracks();
    }
}
