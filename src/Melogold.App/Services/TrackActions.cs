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

    /// <summary>Играющий трек, меню «…» панели плеера.</summary>
    public sealed record Player : TrackContext;
}

/// <summary>
/// Действия с треком: тап по правилу очереди, меню (§5.3, пункты и тексты — как в меню трека Android), «В Избранное»,
/// переходы к альбому и исполнителю. Всё, что показывает меню, известно до его открытия — меню не дёргается (§8.7).
/// </summary>
public sealed class TrackActions(PlayerEngine engine, Library library, Navigator navigator, Snackbar snackbar, YouTubeMusic music,
    TrackDownloads downloads, FileExport export)
{
    // ---------- Воспроизведение ----------

    /// <summary>
    /// Двойной клик или Enter по строке: в списке — весь список с этого трека, иначе — трек и радио. Если в заменяемой
    /// очереди было два и больше треков пользователя — «Очередь заменена · Отменить».
    /// </summary>
    public void Play(IReadOnlyList<Track> tracks, int index, TrackContext context)
    {
        if (index < 0 || index >= tracks.Count) return;
        // Без сети играет только то, что целиком в кэше (tasks/0003 §4)
        if (ViewModels.RowVm.IsOnline?.Invoke() == false && !engine.IsOffline(tracks[index].VideoId))
        {
            snackbar.Show(Loc.Get("NoNetwork"));
            return;
        }
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
        AddItems(menu.Items, track, context, onRemove);
        return menu;
    }

    /// <summary>
    /// Пункты меню трека в порядке всех клиентов (GLOSSARY «Меню трека»): действия с треком, после черты — то, что его
    /// убирает. У играющего трека (<see cref="TrackContext.Player"/>) нет «Играть следующим», «В конец очереди» и ♡:
    /// ♡ рядом, в панели плеера. <paramref name="beforeRemovals"/> добавляет свои группы перед чертой — у плеера это
    /// текст и таймер сна, как на Android (REWRITE §3.10.5). <paramref name="anchor"/> — где показать выбор исполнителя,
    /// если он станет известен только после нажатия.
    /// </summary>
    public void AddItems(IList<MenuFlyoutItemBase> items, Track track, TrackContext context, Action? onRemove = null,
        Action<IList<MenuFlyoutItemBase>>? beforeRemovals = null, FrameworkElement? anchor = null)
    {
        var player = context is TrackContext.Player;

        void Add(string key, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        if (!player)
        {
            Add("MenuPlayNext", "", () => PlayNext(track));
            Add("MenuAddToQueue", "", () => AddToQueue(track));
        }
        Add("MenuAddToPlaylist", "", () => PlaylistPicker.Show(track, library, snackbar));
        AddDownloadItems(items, track, Add);
        if (!player) items.Add(new MenuFlyoutSeparator());
        Add("MenuTrackRadio", "", () => engine.StartRadio(track));
        if (!player)
        {
            var liked = library.IsLiked(track.VideoId);
            Add(liked ? "MenuFavoriteRemove" : "MenuFavoriteAdd", liked ? "" : "", () => ToggleLike(track));
        }
        items.Add(new MenuFlyoutSeparator());
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
            items.Add(sub);
        }
        // У трека без ссылок (из истории, из файла) исполнитель находится при нажатии — так же, как по имени в панели плеера
        else if (anchor is not null && !string.IsNullOrWhiteSpace(track.ArtistsText)) Add(artistKey, "", () => OpenArtist(track, anchor));
        Add("MenuOtherVersions", "", () => OtherVersions(track));
        Add("MenuCopyLink", "", () => CopyLink(track));
        Add("MenuShare", "", () => Share.Track(track));
        beforeRemovals?.Invoke(items);

        var removeKey = context switch
        {
            TrackContext.LocalPlaylist => "MenuRemoveFromPlaylist",
            TrackContext.History => "MenuRemoveFromHistory",
            TrackContext.Queue => "MenuRemoveFromQueue",
            _ => null,
        };
        items.Add(new MenuFlyoutSeparator());
        if (removeKey is not null && onRemove is not null) Add(removeKey, "", onRemove);
        var hidden = library.HiddenTracks().Contains(track.VideoId);
        Add(hidden ? "MenuShowAgain" : "MenuDontShow", "", () =>
        {
            library.SetTrackHidden(track, !hidden);
            if (hidden) return;
            // Играющий трек, который больше не показывать, пропускается, как на Android
            if (player && engine.Current?.VideoId == track.VideoId) engine.Next();
            snackbar.Show(Loc.Get("TrackHidden"), Loc.Get("Undo"), () => library.SetTrackHidden(track, false));
        });
    }

    /// <summary>
    /// «Скачать» по состоянию загрузки (Android <c>DownloadEntry</c>): «Скачать»; пока идёт — «Скачивается 45 % · Отменить»;
    /// после сбоя — «Скачать снова»; скачан — «Удалить загрузку» с «Отменить». И «Сохранить файлом». У трансляции
    /// скачать нечего — пункт неактивен с объяснением.
    /// </summary>
    private void AddDownloadItems(IList<MenuFlyoutItemBase> items, Track track, Action<string, string, Action> add)
    {
        if (track.VideoType == "live")
        {
            items.Add(new MenuFlyoutItem { Text = Loc.Get("MenuDownloadLive"), Icon = new FontIcon { Glyph = "\uE896" }, IsEnabled = false });
            return;
        }
        var state = downloads.State(track.VideoId);
        switch (state?.Status)
        {
            case null:
                add("MenuDownload", "\uE896", () => downloads.Download(track));
                break;
            case DownloadStatus.Completed:
                add("MenuDownloadRemove", "\uE74D", () => snackbar.ShowUndoable(Loc.Get("DownloadRemoved"), () => downloads.Remove(track.VideoId)));
                break;
            case DownloadStatus.Failed:
                add("MenuDownloadRetry", "\uE72C", () => downloads.Retry(track.VideoId));
                break;
            default:
                var cancel = new MenuFlyoutItem
                {
                    Text = state.Value.Progress is { } progress ? Loc.Format("MenuDownloadCancelFormat", (int)(progress * 100)) : Loc.Get("MenuDownloadCancel"),
                    Icon = new FontIcon { Glyph = "\uE711" },
                };
                cancel.Click += (_, _) => downloads.Remove(track.VideoId);
                items.Add(cancel);
                break;
        }
        add("MenuSaveFile", "\uE74E", () => _ = export.SaveAsync(track));
    }

    // ---------- Выделенное (SelectionBar) ----------

    /// <summary>«Слушать»: выделенные по порядку списка — новой очередью.</summary>
    public void PlayAll(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count > 0) Play(tracks, 0, new TrackContext.List());
    }

    public void QueueAll(IReadOnlyList<Track> tracks)
    {
        engine.AddToEnd(tracks);
        snackbar.Show(Loc.Format("QueuedCountFormat", Loc.Plural("Tracks", tracks.Count)));
    }

    public void LikeAll(IReadOnlyList<Track> tracks)
    {
        library.SetLiked(tracks, true);
        snackbar.Show(Loc.Format("LikedCountFormat", Loc.Plural("Tracks", tracks.Count)));
    }

    public void AddAllToPlaylist(IReadOnlyList<Track> tracks) => _ = PlaylistPicker.ShowAsync(tracks, library, snackbar);

    /// <summary>
    /// «Новый плейлист…» из выделенного — так собирается альбом, который после цензуры лежит на YouTube разными видео.
    /// Название по умолчанию — общий альбом выделенного, если он у всех один.
    /// </summary>
    public async Task NewPlaylistAsync(IReadOnlyList<Track> tracks, XamlRoot root)
    {
        var albums = tracks.Select(t => t.AlbumTitle).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();
        var name = await PlaylistDialogs.AskNameAsync(root, Loc.Get("NewPlaylist"), albums.Count == 1 ? albums[0]! : "");
        if (name is null) return;
        var id = library.CreatePlaylist(name, tracks);
        snackbar.Show(Loc.Format("PlaylistCreatedFormat", name, Loc.Plural("Tracks", tracks.Count)), Loc.Get("OpenAction"),
            () => navigator.Open(typeof(LocalPlaylistPage), id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>«Скачать» выделенное: что уже скачано или скачивается и трансляции — пропускаются.</summary>
    public void DownloadAll(IReadOnlyList<Track> tracks)
    {
        var fresh = tracks.Where(t => t.VideoType != "live" && downloads.State(t.VideoId) is null or { Status: DownloadStatus.Failed }).ToList();
        foreach (var track in fresh) downloads.Download(track);
        snackbar.Show(fresh.Count == 0 ? Loc.Get("NothingToDownload") : Loc.Format("DownloadingCountFormat", Loc.Plural("Tracks", fresh.Count)));
    }

    /// <summary>Правый щелчок по выделенному (два трека и больше): те же действия, что на панели выделения.</summary>
    public MenuFlyout BuildSelectionMenu(IReadOnlyList<Track> tracks, XamlRoot root)
    {
        var menu = new MenuFlyout();
        void Add(string key, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("SelectionPlay", "\uE768", () => PlayAll(tracks));
        Add("SelectionQueue", "\uE90B", () => QueueAll(tracks));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("SelectionLike", "\uEB51", () => LikeAll(tracks));
        Add("SelectionAddToPlaylist", "\uE710", () => AddAllToPlaylist(tracks));
        Add("SelectionNewPlaylist", "\uE8F4", () => _ = NewPlaylistAsync(tracks, root));
        Add("SelectionDownload", "\uE896", () => DownloadAll(tracks));
        return menu;
    }

    public void ShowMenu(Track track, TrackContext context, FrameworkElement target, Windows.Foundation.Point? at = null, Action? onRemove = null)
    {
        var menu = BuildMenu(track, context, onRemove);
        if (at is { } point) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }
}
