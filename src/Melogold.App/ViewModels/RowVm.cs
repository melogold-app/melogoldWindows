using CommunityToolkit.Mvvm.ComponentModel;
using Melogold.App.Services;
using Melogold.Core.Music;

namespace Melogold.App.ViewModels;

/// <summary>Список, к которому относится строка: что играет двойной клик и что значит «Убрать из…».</summary>
public sealed class RowOwner(TrackContext context)
{
    public TrackContext Context { get; } = context;

    /// <summary>Треки списка по порядку (для «играть список с этого трека»).</summary>
    public List<Track> Tracks { get; } = [];

    /// <summary>«Убрать из плейлиста / истории / очереди» для строки.</summary>
    public Action<RowVm>? Remove { get; init; }
}

/// <summary>
/// Строка списка (§5.3): трек, видео, альбом, исполнитель, канал или плейлист. У видео квадратная обложка, как у песен:
/// видео и песни — равные источники.
/// </summary>
public sealed partial class RowVm : ObservableObject
{
    public RowVm(MusicItem item, RowOwner owner, bool showType = false)
    {
        Item = item;
        Owner = owner;
        switch (item)
        {
            case Track track:
                Title = track.Title;
                Subtitle = Join(showType ? Loc.Get(track.IsVideo ? "TypeVideo" : "TypeSong") : null, track.ArtistsText,
                    track.IsVideo ? track.ViewsText : track.AlbumTitle);
                ArtworkUrl = Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 120);
                Duration = track.VideoType == "live" ? Loc.Get("Live") : track.DurationText;
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

    public string Title { get; }

    public string Subtitle { get; }

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
