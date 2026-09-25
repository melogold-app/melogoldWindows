using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melogold.App.Views;

/// <summary>
/// «Библиотека» — хаб коллекций (§5.4, REWRITE §3.2.1): Избранное, История, Альбомы, Исполнители и каналы, ниже — свои
/// плейлисты и «Новый плейлист». Всё из локальной базы, без сети. «Скачанное» и «Импорт» — позже.
/// </summary>
public sealed partial class LibraryPage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36), Spacing = 4 };
    private readonly VariableSizedWrapGrid _collections = new() { Orientation = Orientation.Horizontal, ItemWidth = 248, ItemHeight = 76 };
    private readonly GridView _playlists = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly StateView _empty = new() { Visibility = Visibility.Collapsed };
    private readonly Button _importFirst = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 0, 0, 16), Visibility = Visibility.Collapsed };
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private bool _dirty = true;

    public LibraryPage()
    {
        InitializeComponent();
        _content.Children.Add(new TextBlock { Text = Loc.Get("LibraryHeader"), Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        // В пустой библиотеке — сразу кнопка импорта: пришедшим из ViTune больше нечего делать первым
        _importFirst.Content = Loc.Get("LibraryImport");
        _importFirst.Click += async (_, _) => await ImportFlow.RunAsync(XamlRoot);
        _content.Children.Add(_importFirst);
        _content.Children.Add(_collections);

        var header = new Grid { Margin = new Thickness(0, 24, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Loc.Get("ResultsPlaylists"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        var create = new Button { Content = Loc.Get("NewPlaylist") };
        create.Click += async (_, _) => await CreatePlaylistAsync();
        Grid.SetColumn(create, 1);
        header.Children.Add(create);
        _content.Children.Add(header);
        _content.Children.Add(_empty);
        _content.Children.Add(_playlists);

        // Импорт из ViTune или ViMusic (tasks/0004 §4): карточка в конце страницы
        var import = new ClickableCard
        {
            Header = Loc.Get("LibraryImport"),
            Description = Loc.Get("LibraryImportDescription"),
            HeaderIcon = new FontIcon { Glyph = "\uE8B5" },
            Margin = new Thickness(0, 24, 0, 0),
        };
        import.Activated += async (_, _) => await ImportFlow.RunAsync(XamlRoot);
        _content.Children.Add(import);
        _playlists.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:controls="using:Melogold.App.Controls">
                <controls:MediaCard Card="{Binding}" />
            </DataTemplate>
            """);
        _playlists.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is CardVm { Item: LocalPlaylistItem playlist })
                App.Services.GetRequiredService<Navigator>().Open(typeof(LocalPlaylistPage), playlist.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        _scroller.Content = _content;
        Content = _scroller;

        _library.Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Playlists) || change.HasFlag(LibraryChange.Likes) || change.HasFlag(LibraryChange.Bookmarks) || change.HasFlag(LibraryChange.History))
            {
                _dirty = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsLoaded) Refresh();
                });
            }
        };
        Loaded += (_, _) =>
        {
            if (_dirty) Refresh();
        };
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async void Refresh()
    {
        _dirty = false;
        var (counts, playlists, plays, allTracks) = await Task.Run(() => (_library.Counts(), _library.Playlists(), _library.PlayCount(), _library.AllTracksCount()));
        _importFirst.Visibility = counts is { Likes: 0, Albums: 0, Artists: 0 } && playlists.Count == 0 && plays == 0 ? Visibility.Visible : Visibility.Collapsed;
        _collections.Children.Clear();
        // «Все треки» — первой (tasks/0005): прослушанное, лайкнутое и из плейлистов, как «Песни» в ViTune
        AddCollection("\uE8D6", Loc.Get("AllTracks"), Loc.Plural("Tracks", allTracks), () => Open(typeof(AllTracksPage)));
        AddCollection("", Loc.Get("Favorites"), Loc.Plural("Tracks", counts.Likes), () => Open(typeof(FavoritesPage)));
        AddCollection("", Loc.Get("History"), Loc.Get("HistoryHint"), () => Open(typeof(HistoryPage)));
        AddCollection("", Loc.Get("ResultsAlbums"), Loc.Plural("Albums", counts.Albums), () => Open(typeof(SavedPage), "albums"));
        AddCollection("", Loc.Get("ArtistsAndChannels"), Loc.Plural("Artists", counts.Artists), () => Open(typeof(SavedPage), "artists"));

        _playlists.ItemsSource = playlists.Select(p => new CardVm(new LocalPlaylistItem(p))).ToList();
        if (playlists.Count == 0)
        {
            _empty.ShowEmpty("", Loc.Get("NoPlaylistsYet"), Loc.Get("NoPlaylistsHint"));
            _playlists.Visibility = Visibility.Collapsed;
        }
        else
        {
            _empty.ShowContent();
            _playlists.Visibility = Visibility.Visible;
        }
    }

    private static void Open(Type page, string? parameter = null) => App.Services.GetRequiredService<Navigator>().Open(page, parameter);

    private void AddCollection(string glyph, string title, string subtitle, Action open)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button
        {
            Content = grid,
            Width = 240,
            Height = 68,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{title}, {subtitle}");
        button.Click += (_, _) => open();
        _collections.Children.Add(button);
    }

    private async Task CreatePlaylistAsync()
    {
        var name = await PlaylistDialogs.AskNameAsync(XamlRoot, Loc.Get("NewPlaylist"), "");
        if (name is null) return;
        var id = _library.CreatePlaylist(name);
        Open(typeof(LocalPlaylistPage), id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Свой плейлист для карточки хаба: мозаика из обложек первых треков.</summary>
public sealed record LocalPlaylistItem : Core.Music.MusicItem
{
    public LocalPlaylistItem(LocalPlaylist playlist)
    {
        Id = playlist.Id;
        Name = playlist.Name;
        Count = playlist.TrackCount;
        Cover = playlist.ThumbnailUrl ?? playlist.Mosaic.FirstOrDefault();
    }

    public long Id { get; }
    public string Name { get; }
    public int Count { get; }
    public string? Cover { get; }
}

/// <summary>Диалоги своих плейлистов: название (новый, переименовать).</summary>
public static class PlaylistDialogs
{
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string current)
    {
        var box = new TextBox { Text = current, PlaceholderText = Loc.Get("NewPlaylistName"), MaxLength = 200 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = Loc.Get("Done"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = current.Trim().Length > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
