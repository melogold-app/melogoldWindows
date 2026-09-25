using System.Globalization;
using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>Фильтр и сортировка длинного списка (§5.3): сортировка запоминается для экрана.</summary>
public sealed partial class ListToolbar : Grid
{
    private readonly TextBox _filter = new() { PlaceholderText = Loc.Get("Filter"), Width = 240 };
    private readonly ComboBox? _sort;

    public ListToolbar(string screen, IReadOnlyList<(string Key, string Label)> sorts, Action changed)
    {
        ColumnSpacing = 8;
        Margin = new Thickness(0, 0, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_filter, Loc.Get("Filter"));
        _filter.TextChanged += (_, _) => changed();
        Children.Add(_filter);
        if (sorts.Count > 0)
        {
            var settings = App.Services.GetRequiredService<SettingsStore>();
            _sort = new ComboBox { MinWidth = 200, Header = null };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_sort, Loc.Get("Sort"));
            foreach (var (key, label) in sorts) _sort.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            var saved = settings.Sorts.GetValueOrDefault(screen);
            _sort.SelectedIndex = Math.Max(0, sorts.ToList().FindIndex(s => s.Key == saved));
            _sort.SelectionChanged += (_, _) =>
            {
                settings.SetSort(screen, SortKey);
                changed();
            };
            SetColumn(_sort, 2);
            Children.Add(_sort);
        }
    }

    public string Filter => _filter.Text.Trim();

    public string SortKey => (_sort?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    public static bool Matches(Track track, string filter) =>
        filter.Length == 0 || track.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        track.ArtistsText?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
        track.AlbumTitle?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;

    public static IEnumerable<Track> Sorted(IEnumerable<Track> tracks, string key) => key switch
    {
        "title" => tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        "artist" => tracks.OrderBy(t => t.ArtistsText ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => tracks,
    };
}

/// <summary>Избранное (REWRITE §3.2.2): список играет целиком, фильтр и сортировка.</summary>
public sealed partial class FavoritesPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly CollectionHeader _header = new();
    private readonly ListToolbar _toolbar;
    private readonly StateView _state = new();
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private List<Track> _all = [];

    public FavoritesPage()
    {
        InitializeComponent();
        _toolbar = new ListToolbar("favorites", [("recent", Loc.Get("SortRecent")), ("title", Loc.Get("SortTitle")), ("artist", Loc.Get("SortArtist"))], Show);
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_toolbar);
        top.Children.Add(_state);
        _list.Header = top;
        Content = _list;
        var actions = App.Services.GetRequiredService<TrackActions>();
        _header.AddButton(Loc.Get("PlayAll"), "", () => actions.Play(Visible(), 0, new TrackContext.List()), accent: true);
        _header.AddButton(Loc.Get("Shuffle"), "", () => actions.PlayShuffled(Visible()));
        _library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Likes)) DispatcherQueue.TryEnqueue(Load);
        };
        Loaded += (_, _) => Load();
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private async void Load()
    {
        _all = await Task.Run(_library.Favorites);
        _header.Set(Loc.Get("Favorites"), Loc.Plural("Tracks", _all.Count), null, null);
        Show();
    }

    private List<Track> Visible() => ListToolbar.Sorted(_all.Where(t => ListToolbar.Matches(t, _toolbar.Filter)), _toolbar.SortKey).ToList();

    private void Show()
    {
        var visible = Visible();
        _list.SetItems(visible, new RowOwner(new TrackContext.List()));
        if (_all.Count == 0) _state.ShowEmpty("", Loc.Get("FavoritesEmpty"), Loc.Get("FavoritesEmptyHint"), (Loc.Get("FindMusic"), () => App.Current?.Window?.FocusSearchBox()));
        else if (visible.Count == 0) _state.ShowEmpty("", Loc.Get("NothingFound"));
        else _state.ShowContent();
    }
}

/// <summary>
/// История (REWRITE §3.2.4): «Недавние» (трек и радио) и «Чаще всего» с периодом 7 дней · 30 дней · Год · Всё время
/// (список играет целиком). «Убрать из истории» — с «Отменить»; «Очистить историю…» — с диалогом.
/// </summary>
public sealed partial class HistoryPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly SelectorBar _mode = new();
    private readonly SelectorBar _period = new();
    private readonly StateView _state = new();
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly HashSet<string> _pendingRemoval = [];

    public HistoryPage()
    {
        InitializeComponent();
        var top = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Loc.Get("History"), Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        var clear = new Button { Content = Loc.Get("ClearHistory"), VerticalAlignment = VerticalAlignment.Top };
        clear.Click += async (_, _) => await ClearAsync();
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);
        top.Children.Add(header);
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("HistoryRecent"), Tag = "recent" });
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("HistoryMostPlayed"), Tag = "top" });
        foreach (var (key, days) in new[] { ("Days7", 7), ("Days30", 30), ("Year", 365), ("AllTime", 0) })
            _period.Items.Add(new SelectorBarItem { Text = Loc.Get(key), Tag = days });
        _mode.SelectedItem = _mode.Items[0];
        _period.SelectedItem = _period.Items[1];
        _mode.SelectionChanged += (_, _) => Load();
        _period.SelectionChanged += (_, _) => Load();
        top.Children.Add(_mode);
        top.Children.Add(_period);
        top.Children.Add(_state);
        _list.Header = top;
        Content = _list;
        _library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.History)) DispatcherQueue.TryEnqueue(Load);
        };
        Loaded += (_, _) => Load();
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private bool Recent => (string?)_mode.SelectedItem?.Tag != "top";

    private async void Load()
    {
        _period.Visibility = Recent ? Visibility.Collapsed : Visibility.Visible;
        var recent = Recent;
        var days = (int?)_period.SelectedItem?.Tag ?? 30;
        var tracks = await Task.Run(() => recent
            ? _library.RecentHistory().Select(h => h.Track).ToList()
            : _library.MostPlayed(days == 0 ? null : IsoTime.NowMs() - days * 86_400_000L).Select(t => t.Track).ToList());
        tracks = tracks.Where(t => !_pendingRemoval.Contains(t.VideoId)).ToList();
        _list.SetItems(tracks, new RowOwner(new TrackContext.History(PlaysList: !recent)) { Remove = RemoveRow });
        if (tracks.Count == 0) _state.ShowEmpty("", Loc.Get("HistoryEmpty"), Loc.Get("HistoryEmptyHint"));
        else _state.ShowContent();
    }

    private void RemoveRow(RowVm row)
    {
        if (row.Track is not { } track) return;
        var index = _list.Entries.IndexOf(row);
        _list.Entries.Remove(row);
        _pendingRemoval.Add(track.VideoId);
        App.Services.GetRequiredService<Snackbar>().ShowUndoable(Loc.Format("RemovedFromHistoryFormat", track.Title),
            commit: () =>
            {
                _pendingRemoval.Remove(track.VideoId);
                _ = Task.Run(() => _library.RemoveFromHistory(track.VideoId));
            },
            undo: () =>
            {
                _pendingRemoval.Remove(track.VideoId);
                _list.Entries.Insert(Math.Min(index, _list.Entries.Count), row);
            });
    }

    private async Task ClearAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("ClearHistoryTitle"),
            Content = Loc.Get("ClearHistoryText"),
            PrimaryButtonText = Loc.Get("Clear"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(_library.ClearHistory);
    }
}

/// <summary>
/// Свой плейлист (REWRITE §3.8.1): порядок меняется перетаскиванием и Alt+↑/↓, «Убрать из плейлиста» и «Удалить
/// плейлист» — с «Отменить», «Переименовать».
/// </summary>
public sealed partial class LocalPlaylistPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24), CanReorderItems = true, AllowDrop = true };
    private readonly CollectionHeader _header = new();
    private readonly ListToolbar _toolbar;
    private readonly StateView _state = new();
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly HashSet<string> _pendingRemoval = [];
    private long _id;
    private LocalPlaylist? _playlist;
    private List<Track> _tracks = [];
    private bool _headerReady;

    public LocalPlaylistPage()
    {
        InitializeComponent();
        _toolbar = new ListToolbar("playlist", [], Show);
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_toolbar);
        top.Children.Add(_state);
        _list.Header = top;
        _list.DragItemsCompleted += (_, _) => SaveOrder();
        _list.KeyDown += OnListKeyDown;
        Content = _list;
        _library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Playlists)) DispatcherQueue.TryEnqueue(Load);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _id = long.TryParse(Navigator.Resolve(e.Parameter) as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        Load();
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private async void Load()
    {
        var id = _id;
        var (playlist, tracks) = await Task.Run(() => (_library.GetPlaylist(id), _library.PlaylistTracks(id)));
        if (playlist is null)
        {
            _state.ShowEmpty("", Loc.Get("PlaylistGone"));
            return;
        }
        _playlist = playlist;
        _tracks = tracks.Where(t => !_pendingRemoval.Contains(t.VideoId)).ToList();
        var duration = _tracks.Sum(t => t.DurationMs ?? 0);
        _header.Set(playlist.Name, Loc.Plural("Tracks", _tracks.Count), duration > 0 ? Durations.Format(duration) : null,
            Thumbnails.Sized(playlist.ThumbnailUrl ?? playlist.Mosaic.FirstOrDefault(), 400));
        if (!_headerReady) BuildButtons();
        Show();
    }

    private void BuildButtons()
    {
        _headerReady = true;
        var actions = App.Services.GetRequiredService<TrackActions>();
        _header.AddButton(Loc.Get("PlayAll"), "", () => actions.Play(_tracks, 0, new TrackContext.List()), accent: true);
        _header.AddButton(Loc.Get("Shuffle"), "", () => actions.PlayShuffled(_tracks));
        var menu = App.Services.GetRequiredService<CollectionMenu>().Build(() => Task.FromResult<IReadOnlyList<Track>>(_tracks),
            _playlist?.BrowseId is { } browse ? $"https://www.youtube.com/playlist?list={browse}" : null);
        menu.Items.Add(new MenuFlyoutSeparator());
        var rename = new MenuFlyoutItem { Text = Loc.Get("Rename"), Icon = new FontIcon { Glyph = "" } };
        rename.Click += async (_, _) =>
        {
            if (_playlist is null) return;
            var name = await PlaylistDialogs.AskNameAsync(XamlRoot, Loc.Get("Rename"), _playlist.Name);
            if (name is not null) _library.RenamePlaylist(_playlist.Id, name);
        };
        menu.Items.Add(rename);
        var delete = new MenuFlyoutItem { Text = Loc.Get("DeletePlaylist"), Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) => DeletePlaylist();
        menu.Items.Add(delete);
        _header.AddMenu(menu);
    }

    private void Show()
    {
        var filter = _toolbar.Filter;
        var visible = _tracks.Where(t => ListToolbar.Matches(t, filter)).ToList();
        _list.CanReorderItems = filter.Length == 0;
        _list.SetItems(visible, new RowOwner(new TrackContext.LocalPlaylist(_id)) { Remove = RemoveRow });
        if (_tracks.Count == 0) _state.ShowEmpty("", Loc.Get("PlaylistEmpty"), Loc.Get("PlaylistEmptyHint"));
        else if (visible.Count == 0) _state.ShowEmpty("", Loc.Get("NothingFound"));
        else _state.ShowContent();
    }

    /// <summary>Порядок после перетаскивания — в базу (синхронизация отправит перемещения мелкими ops).</summary>
    private void SaveOrder()
    {
        var order = _list.Rows.Select(r => r.Track!).ToList();
        var id = _id;
        _ = Task.Run(() =>
        {
            for (var i = 0; i < order.Count; i++) _library.MoveInPlaylist(id, order[i].VideoId, i);
        });
    }

    /// <summary>Alt+↑ / Alt+↓ — выбранный трек выше или ниже (§5.3).</summary>
    private void OnListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_list.CanReorderItems || _list.SelectedItem is not RowVm row || row.Track is not { } track) return;
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt || e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        var index = _list.Entries.IndexOf(row);
        var target = e.Key == Windows.System.VirtualKey.Up ? index - 1 : index + 1;
        if (target < 0 || target >= _list.Entries.Count) return;
        _list.Entries.Move(index, target);
        _list.SelectedItem = row;
        _library.MoveInPlaylist(_id, track.VideoId, target);
        e.Handled = true;
    }

    private void RemoveRow(RowVm row)
    {
        if (row.Track is not { } track) return;
        var index = _list.Entries.IndexOf(row);
        _list.Entries.Remove(row);
        _tracks.Remove(track);
        _pendingRemoval.Add(track.VideoId);
        var id = _id;
        App.Services.GetRequiredService<Snackbar>().ShowUndoable(Loc.Format("RemovedFromPlaylistFormat", track.Title),
            commit: () =>
            {
                _pendingRemoval.Remove(track.VideoId);
                _library.RemoveFromPlaylist(id, track.VideoId);
            },
            undo: () =>
            {
                _pendingRemoval.Remove(track.VideoId);
                Load();
            });
    }

    private void DeletePlaylist()
    {
        if (_playlist is not { } playlist) return;
        App.Services.GetRequiredService<Navigator>().GoBack();
        App.Services.GetRequiredService<Snackbar>().ShowUndoable(Loc.Format("PlaylistDeletedFormat", playlist.Name),
            commit: () => _library.DeletePlaylist(playlist.Id),
            undo: () => App.Services.GetRequiredService<Navigator>().Open(typeof(LocalPlaylistPage), playlist.Id.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>Сохранённые альбомы или исполнители и каналы (REWRITE §3.2.6–§3.2.7): сетка карточек.</summary>
public sealed partial class SavedPage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly StateView _state = new();
    private readonly Border _grid = new();
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private string _kind = "albums";

    public SavedPage()
    {
        InitializeComponent();
        _content.Children.Add(_title);
        _content.Children.Add(_state);
        _content.Children.Add(_grid);
        _scroller.Content = _content;
        Content = _scroller;
        _library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Bookmarks)) DispatcherQueue.TryEnqueue(Load);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _kind = Navigator.Resolve(e.Parameter) as string ?? "albums";
        Load();
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async void Load()
    {
        var albums = _kind == "albums";
        _title.Text = Loc.Get(albums ? "ResultsAlbums" : "ArtistsAndChannels");
        var items = await Task.Run<List<MusicItem>>(() => albums ? [.. _library.SavedAlbums()] : [.. _library.SavedArtists()]);
        _grid.Child = ShelfView.CardGrid(items);
        if (items.Count == 0) _state.ShowEmpty(albums ? "" : "", Loc.Get(albums ? "AlbumsEmpty" : "ArtistsEmpty"), Loc.Get(albums ? "AlbumsEmptyHint" : "ArtistsEmptyHint"));
        else _state.ShowContent();
    }
}
