using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace Melogold.App.Services;

/// <summary>Откуда показан трек: от этого зависят «Убрать из…» и правило очереди (REWRITE §2.3).</summary>
public abstract record TrackContext
{
    /// <summary>Тап играет трек и радио, а не весь список (REWRITE §2.3).</summary>
    public virtual bool PlaysSingle => false;

    /// <summary>Выдача поиска, ссылка: одиночный трек и радио.</summary>
    public sealed record Single : TrackContext
    {
        public override bool PlaysSingle => true;
    }

    /// <summary>Альбом, плейлист YouTube, Избранное: играет весь список.</summary>
    public sealed record List : TrackContext;

    public sealed record LocalPlaylist(long Id) : TrackContext;

    /// <summary>История: «Недавние» — трек и радио, «Чаще всего» — список.</summary>
    public sealed record History(bool PlaysList) : TrackContext
    {
        public override bool PlaysSingle => !PlaysList;
    }

    public sealed record Queue(long ItemId) : TrackContext;
}

/// <summary>
/// Действия с треком: тап по правилу очереди, меню (§5.3, пункты и тексты — как в меню трека Android), «В Избранное»,
/// переходы к альбому и исполнителю. Всё, что показывает меню, известно до его открытия — меню не дёргается (§8.7).
/// </summary>
public sealed class TrackActions(PlayerEngine engine, Library library, Navigator navigator, Snackbar snackbar, YouTubeMusic music)
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
        if (context.PlaysSingle) engine.PlaySingle(tracks[index]);
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

    public async void OpenAlbumOrArtist(Track track)
    {
        if (track.AlbumId is null && !track.Artists.Any(a => a.Id is not null)) track = await CompleteAsync(track);
        if (track.AlbumId is { } album) navigator.Open(typeof(AlbumPage), album);
        else if (track.Artists.FirstOrDefault(a => a.Id is not null) is { } artist) navigator.Open(typeof(ArtistPage), artist.Id);
    }

    /// <summary>
    /// Исполнитель трека; если их несколько — меню выбора у <paramref name="anchor"/>. У трека без ссылок на
    /// исполнителей (из истории, из файла) они берутся из YouTube Music, а если и там нет — открывается поиск по имени.
    /// </summary>
    public async void OpenArtist(Track track, FrameworkElement anchor)
    {
        var artists = track.Artists.Where(a => a.Id is not null).ToList();
        if (artists.Count == 0) artists = (await CompleteAsync(track)).Artists.Where(a => a.Id is not null).ToList();
        if (artists.Count == 1)
        {
            navigator.Open(typeof(ArtistPage), artists[0].Id);
            return;
        }
        if (artists.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(track.ArtistsText)) navigator.Open(typeof(SearchPage), new SearchRequest(track.ArtistsText, track.IsVideo ? SearchScope.YouTube : SearchScope.Music));
            return;
        }
        var flyout = new MenuFlyout();
        foreach (var artist in artists)
        {
            var item = new MenuFlyoutItem { Text = artist.Name };
            item.Click += (_, _) => navigator.Open(typeof(ArtistPage), artist.Id);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>Трек с альбомом и исполнителями из «Далее» YouTube Music; без сети — как был.</summary>
    private async Task<Track> CompleteAsync(Track track)
    {
        try
        {
            var page = await music.NextAsync(track.VideoId);
            return page.Tracks.FirstOrDefault(t => t.VideoId == track.VideoId) is { } found && found.Artists.Any(a => a.Id is not null)
                ? track with { Artists = found.Artists, AlbumId = track.AlbumId ?? found.AlbumId, AlbumTitle = track.AlbumTitle ?? found.AlbumTitle }
                : track;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or YouTubeException or System.Text.Json.JsonException)
        {
            Log.Warn($"Artists of {track.VideoId} unavailable", e);
            return track;
        }
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
