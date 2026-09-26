using CommunityToolkit.Mvvm.ComponentModel;
using Melogold.App.Services;
using Melogold.Core.Music;

namespace Melogold.App.ViewModels;

/// <summary>
/// Метка строки (Android <c>DownloadBadge</c>, REWRITE §3.11.11): скачан — закрашенный значок, скачивается — кольцо
/// с долей, ждёт очереди или сбой — свои значки; без загрузки — «есть без сети», если трек целиком в кэше (тот же значок
/// контуром: его могут сменить новые треки).
/// </summary>
public enum OfflineMark
{
    None,
    Cached,
    Queued,
    Downloading,
    Downloaded,
    Failed,
}

/// <summary>Список, к которому относится строка: что играет двойной клик и что значит «Убрать из…».</summary>
public sealed class RowOwner(TrackContext context)
{
    public TrackContext Context { get; } = context;

    /// <summary>Треки списка по порядку (для «играть список с этого трека»).</summary>
    public List<Track> Tracks { get; } = [];

    /// <summary>«Убрать из плейлиста / истории / очереди» для строки.</summary>
    public Action<RowVm>? Remove { get; init; }

    /// <summary>Вторая строка после исполнителя вместо альбома — например, сколько трек слушали («Все треки»).</summary>
    public Func<Track, string?>? Detail { get; init; }
}

/// <summary>
/// Строка списка (§5.3): трек, видео, альбом, исполнитель, канал или плейлист. У видео квадратная обложка, как у песен:
/// видео и песни — равные источники.
/// </summary>
public sealed partial class RowVm : ObservableObject
{
    /// <summary>Трек целиком в кэше музыки (задаётся при запуске приложения).</summary>
    public static Func<string, bool>? IsCached { get; set; }

    /// <summary>Есть ли сеть (задаётся при запуске приложения).</summary>
    public static Func<bool>? IsOnline { get; set; }

    /// <summary>Загрузка трека (задаётся при запуске приложения).</summary>
    public static Func<string, Melogold.Playback.DownloadState?>? DownloadOf { get; set; }

    /// <summary>Есть без сети: скачан или целиком в кэше.</summary>
    public bool Cached => Mark is OfflineMark.Cached or OfflineMark.Downloaded;

    /// <summary>Метка у длительности; меняется на ходу (загрузка идёт, кэш дописан).</summary>
    [ObservableProperty]
    public partial OfflineMark Mark { get; set; }

    /// <summary>Доля скачанного для кольца (0…100); null — ещё неизвестна.</summary>
    [ObservableProperty]
    public partial double? Progress { get; set; }

    /// <summary>Метка заново: загрузка или кэш трека изменились.</summary>
    public void RefreshMark()
    {
        if (Track is not { } track) return;
        var download = DownloadOf?.Invoke(track.VideoId);
        Progress = download?.Progress is { } fraction ? fraction * 100 : null;
        Mark = download?.Status switch
        {
            Melogold.Playback.DownloadStatus.Completed => OfflineMark.Downloaded,
            Melogold.Playback.DownloadStatus.Downloading => OfflineMark.Downloading,
            Melogold.Playback.DownloadStatus.Queued => OfflineMark.Queued,
            Melogold.Playback.DownloadStatus.Failed => OfflineMark.Failed,
            _ => IsCached?.Invoke(track.VideoId) == true ? OfflineMark.Cached : OfflineMark.None,
        };
    }

    /// <summary>Сети нет, а трека нет в кэше: строка приглушена, воспроизвести его нельзя.</summary>
    public bool Dimmed { get; }

    public RowVm(MusicItem item, RowOwner owner, bool showType = false)
    {
        Item = item;
        Owner = owner;
        switch (item)
        {
            case Track track:
                Title = track.Title;
                Subtitle = owner.Detail is { } detail
                    ? Join(track.ArtistsText, detail(track))
                    : Join(showType ? Loc.Get(track.IsVideo ? "TypeVideo" : "TypeSong") : null, track.ArtistsText,
                        track.IsVideo ? track.ViewsText : track.AlbumTitle);
                ArtworkUrl = Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 120);
                Duration = track.VideoType == "live" ? Loc.Get("Live") : track.DurationText;
                // Скачан или целиком в кэше — играет без сети; без сети остальные приглушены (tasks/0003 §4)
                RefreshMark();
                Dimmed = !Cached && IsOnline?.Invoke() == false;
                Explicit = track.Explicit;
                Unavailable = track.Unavailable;
                break;
            case AlbumItem album:
                Title = album.Title;
                Subtitle = Join(album.TypeText ?? Loc.Get("TypeAlbum"), album.ArtistsText, album.Year);
                ArtworkUrl = Thumbnails.Sized(album.ThumbnailUrl, 120);
                Explicit = album.Explicit;
                break;
            case ArtistItem artist:
                Title = artist.Name;
                Subtitle = Join(Loc.Get(artist.IsChannel ? "TypeChannel" : "TypeArtist"), artist.Subtitle);
                ArtworkUrl = Thumbnails.Sized(artist.ThumbnailUrl, 120);
                IsRound = true;
                break;
            case PlaylistItem playlist:
                Title = playlist.Title;
                Subtitle = Join(Loc.Get("TypePlaylist"), playlist.Subtitle);
                ArtworkUrl = Thumbnails.Sized(playlist.ThumbnailUrl, 120);
                break;
            default:
                Title = "";
                break;
        }
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public MusicItem Item { get; }

    public RowOwner Owner { get; }

    public Track? Track => Item as Track;

    public bool IsTrack => Item is Track;

    public string Title { get; } = "";

    public string Subtitle { get; } = "";

    public string? ArtworkUrl { get; }

    public bool IsRound { get; }

    public string? Duration { get; }

    public bool Explicit { get; }

    public bool Unavailable { get; }

    [ObservableProperty]
    public partial bool IsLiked { get; set; }

    /// <summary>Строка текущего трека (подсвечивается).</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Для экранного диктора: строка называет себя целиком (§5.5).</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, Subtitle, Duration }.Where(s => !string.IsNullOrEmpty(s)));
}
