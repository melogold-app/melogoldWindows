using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace Melogold.App.Services;

/// <summary>Откуда показан трек: от этого зависят «Убрать из…» и правило очереди (REWRITE §2.3).</summary>
public abstract record TrackContext
{
    /// <summary>Выдача поиска, «Недавние», ссылка: одиночный трек и радио.</summary>
    public sealed record Single : TrackContext;

    /// <summary>Альбом, плейлист YouTube, Избранное, «Чаще всего»: играет весь список.</summary>
    public sealed record List : TrackContext;

    public sealed record LocalPlaylist(long Id) : TrackContext;

    public sealed record History : TrackContext;

    public sealed record Queue(long ItemId) : TrackContext;
}

/// <summary>
/// Действия с треком: тап по правилу очереди, меню (§5.3, пункты и тексты — как в меню трека Android), «В Избранное»,
/// переходы к альбому и исполнителю. Всё, что показывает меню, известно до его открытия — меню не дёргается (§8.7).
/// </summary>
public sealed class TrackActions(PlayerEngine engine, Library library, Navigator navigator, Snackbar snackbar)
{
    // ---------- Воспроизведение ----------

    /// <summary>
    /// Двойной клик или Enter по строке: в списке — весь список с этого трека, иначе — трек и радио. Если в заменяемой
    /// очереди было два и больше треков пользователя — «Очередь заменена · Отменить».
    /// </summary>
    public void Play(IReadOnlyList<Track> tracks, int index, TrackContext context)
    {
        if (index < 0 || index >= tracks.Count) return;
        var previous = engine.Queue.UserAddedCount >= 2 ? engine.Queue.Snapshot((long)engine.Position.TotalMilliseconds) : null;
        var wasPlaying = engine.IsPlaying;
        if (context is TrackContext.Single) engine.PlaySingle(tracks[index]);
        else engine.PlayList(tracks, index);
        if (previous is not null)
        {
            snackbar.Show(Loc.Get("QueueReplaced"), Loc.Get("Undo"), () =>
            {
                engine.Restore(previous);
                if (wasPlaying) engine.Play();
            }, TimeSpan.FromSeconds(5));
        }
    }

    public void PlayShuffled(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        engine.PlayList(tracks, Random.Shared.Next(tracks.Count), shuffle: true);
    }

    public void PlayNext(Track track)
    {
        engine.PlayNext([track]);
        snackbar.Show(Loc.Format("PlayingNextFormat", track.Title));
    }

    public void AddToQueue(Track track)
    {
        engine.AddToEnd([track]);
        snackbar.Show(Loc.Format("AddedToQueueFormat", track.Title));
    }

    public void ToggleLike(Track track) => library.SetLiked(track, !library.IsLiked(track.VideoId));

    // ---------- Переходы ----------

    public void OpenAlbumOrArtist(Track track)
    {
        if (track.AlbumId is { } album) navigator.Open(typeof(AlbumPage), album);
        else if (track.Artists.FirstOrDefault(a => a.Id is not null) is { } artist) navigator.Open(typeof(ArtistPage), artist.Id);
    }

    /// <summary>Исполнитель трека; если их несколько — меню выбора у <paramref name="anchor"/>.</summary>
    public void OpenArtist(Track track, FrameworkElement anchor)
    {
        var artists = track.Artists.Where(a => a.Id is not null).ToList();
        if (artists.Count == 1)
        {
            navigator.Open(typeof(ArtistPage), artists[0].Id);
            return;
        }
        if (artists.Count == 0) return;
        var flyout = new MenuFlyout();
        foreach (var artist in artists)
        {
            var item = new MenuFlyoutItem { Text = artist.Name };
            item.Click += (_, _) => navigator.Open(typeof(ArtistPage), artist.Id);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    public void OtherVersions(Track track)
    {
        var clean = TitleCleaner.Clean(track.Title, track.ArtistsText, track.VideoType);
        navigator.Open(typeof(SearchPage), new SearchRequest(string.Join(" ", new[] { clean.Artist, clean.Title }.Where(s => !string.IsNullOrEmpty(s))), SearchScope.YouTube));
    }

    public void CopyLink(Track track)
    {
        var package = new DataPackage();
        package.SetText(track.IsVideo ? $"https://www.youtube.com/watch?v={track.VideoId}" : $"https://music.youtube.com/watch?v={track.VideoId}");
        Clipboard.SetContent(package);
        snackbar.Show(Loc.Get("LinkCopied"));
    }

    // ---------- Меню ----------

    public MenuFlyout BuildMenu(Track track, TrackContext context, Action? onRemove = null)
    {
        var menu = new MenuFlyout();

        void Add(string key, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("MenuPlayNext", "", () => PlayNext(track));
        Add("MenuAddToQueue", "", () => AddToQueue(track));
        Add("MenuAddToPlaylist", "", () => PlaylistPicker.Show(track, library, snackbar));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("MenuTrackRadio", "", () => engine.StartRadio(track));
        var liked = library.IsLiked(track.VideoId);
        Add(liked ? "MenuFavoriteRemove" : "MenuFavoriteAdd", liked ? "" : "", () => ToggleLike(track));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (track.AlbumId is { } album) Add("MenuGoToAlbum", "", () => navigator.Open(typeof(AlbumPage), album));
        var artists = track.Artists.Where(a => a.Id is not null).ToList();
        var artistKey = track.IsVideo ? "MenuGoToChannel" : "MenuGoToArtist";
        if (artists.Count == 1) Add(artistKey, "", () => navigator.Open(typeof(ArtistPage), artists[0].Id));
        else if (artists.Count > 1)
        {
            var sub = new MenuFlyoutSubItem { Text = Loc.Get(artistKey), Icon = new FontIcon { Glyph = "" } };
            foreach (var artist in artists)
            {
                var item = new MenuFlyoutItem { Text = artist.Name };
                item.Click += (_, _) => navigator.Open(typeof(ArtistPage), artist.Id);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        Add("MenuOtherVersions", "", () => OtherVersions(track));
        Add("MenuCopyLink", "", () => CopyLink(track));

        var removeKey = context switch
        {
            TrackContext.LocalPlaylist => "MenuRemoveFromPlaylist",
            TrackContext.History => "MenuRemoveFromHistory",
            TrackContext.Queue => "MenuRemoveFromQueue",
            _ => null,
        };
        menu.Items.Add(new MenuFlyoutSeparator());
        if (removeKey is not null && onRemove is not null) Add(removeKey, "", onRemove);
        var hidden = library.HiddenTracks().Contains(track.VideoId);
        Add(hidden ? "MenuShowAgain" : "MenuDontShow", "", () =>
        {
            library.SetTrackHidden(track, !hidden);
            if (!hidden) snackbar.Show(Loc.Get("TrackHidden"), Loc.Get("Undo"), () => library.SetTrackHidden(track, false));
        });
        return menu;
    }

    public void ShowMenu(Track track, TrackContext context, FrameworkElement target, Windows.Foundation.Point? at = null, Action? onRemove = null)
    {
        var menu = BuildMenu(track, context, onRemove);
        if (at is { } point) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }
}
