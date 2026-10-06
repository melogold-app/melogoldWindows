using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// Исполнитель YouTube Music одной прокруткой (REWRITE §3.7.1), как в Apple Music (tasks/0024): фото во всю ширину с
/// именем и круглыми кнопками ⓘ ▶ ⊕ … (<see cref="ArtistHero"/>), ниже популярные треки — в две-три колонки на широком
/// окне, — альбомы, синглы, видео и похожие исполнители полками. Фото ушло под верх — сверху полоса с именем и ▶ на
/// системном фоне. ⓘ — лист <see cref="ArtistAboutDialog"/>. Канал обычного YouTube (§3.7.2) — с прежней шапкой.
/// </summary>
public sealed partial class ArtistPage : CatalogPage
{
    private const double NarrowWidth = 600;
    private const double BarHeight = 52;
    private readonly ScrollViewer _scroller = new();
    private readonly ArtistHero _hero = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly CollectionHeader _header = new();
    private readonly StateView _state = new();
    private readonly StackPanel _shelves = new();
    private readonly Grid _bar = new()
    {
        Height = BarHeight,
        VerticalAlignment = VerticalAlignment.Top,
        ColumnSpacing = 12,
        Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(0, 0, 0, 1),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _barName = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _barPlay = new() { Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
    private Func<Task>? _play;
    private string _browseId = "";

    public ArtistPage()
    {
        InitializeComponent();
        var page = new StackPanel();
        page.Children.Add(_hero);
        page.Children.Add(_content);
        _content.Children.Add(_header);
        _content.Children.Add(_state);
        _content.Children.Add(_shelves);
        _header.Visibility = Visibility.Collapsed;
        _scroller.Content = page;
        _scroller.ViewChanged += (_, _) => UpdateBar();

        _barPlay.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _barPlay.Content = new FontIcon { Glyph = "", FontSize = 14 };
        ToolTipService.SetToolTip(_barPlay, Loc.Get("PlayAll"));
        AutomationProperties.SetName(_barPlay, Loc.Get("PlayAll"));
        _barPlay.Click += async (_, _) =>
        {
            if (_play is { } play) await play();
        };
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bar.Children.Add(_barPlay);
        Grid.SetColumn(_barName, 1);
        _bar.Children.Add(_barName);

        var root = new Grid();
        root.Children.Add(_scroller);
        root.Children.Add(_bar);
        Content = root;
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _browseId = Navigator.Resolve(e.Parameter) as string ?? "";
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    /// <summary>Поля — как у остальных страниц: уже 600 — 16, шире — 36; под фото отступ меньше.</summary>
    private void Fit(double width)
    {
        var side = width < NarrowWidth ? 16 : 36;
        _content.Margin = new Thickness(side, _hero.Visibility == Visibility.Visible ? 8 : 24, side, 36);
        _bar.Padding = new Thickness(side, 0, side, 0);
    }

    /// <summary>Фото ушло под верх страницы — полоса с именем и ▶; вернулось — полосы нет.</summary>
    private void UpdateBar()
    {
        var show = _hero.Visibility == Visibility.Visible && _scroller.VerticalOffset > _hero.ActualHeight - BarHeight;
        _bar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var page = await DetailsAsync(_browseId, ct);
        ct.ThrowIfCancellationRequested();
        var library = App.Services.GetRequiredService<Library>();
        var actions = App.Services.GetRequiredService<TrackActions>();
        var topTracks = TopTracks(page);
        Task<IReadOnlyList<Track>> Songs() => SongsAsync(page);
        var artist = new ArtistItem { BrowseId = _browseId, Name = page.Name, ThumbnailUrl = page.ThumbnailUrl, IsChannel = page.IsChannel };
        var menu = topTracks.Count > 0
            ? App.Services.GetRequiredService<CollectionMenu>().Build(Songs, new ShareTarget(page.Name, null, ShareLinks.Artist(_browseId, page.IsChannel)))
            : null;
        _play = topTracks.Count > 0 ? async () => actions.Play(await Songs(), 0, new TrackContext.List()) : null;

        _header.ClearButtons();
        _hero.ClearButtons();
        if (page.IsChannel)
        {
            _header.Set(page.Name, Loc.Get("YouTubeChannel"), page.SubscribersText, Thumbnails.Sized(page.ThumbnailUrl, 400), round: true, description: page.Description);
            if (_play is { } play)
            {
                _header.AddButton(Loc.Get("PlayAll"), "", () => _ = play(), accent: true);
                _header.AddButton(Loc.Get("Shuffle"), "", async () => actions.PlayShuffled(await Songs()));
            }
            _header.AddToggle(on => Loc.Get(on ? "Subscribed" : "Subscribe"), library.IsArtistSaved(_browseId), on => library.SetArtistSaved(artist, on));
            if (menu is not null) _header.AddMenu(menu);
            _header.Visibility = Visibility.Visible;
            _hero.Visibility = Visibility.Collapsed;
        }
        else
        {
            _hero.Visibility = Visibility.Visible;
            _hero.Set(page.Name, page.ThumbnailUrl, XamlRoot?.RasterizationScale ?? 1);
            _hero.AddButton("", Loc.Get("ArtistAbout"), () =>
            {
                if (XamlRoot is { } root) _ = ArtistAboutDialog.ShowAsync(root, page);
            });
            if (_play is { } play) _hero.AddButton("", Loc.Get("PlayAll"), () => _ = play(), accent: true);
            _hero.AddToggle(on => Loc.Get(on ? "Subscribed" : "Subscribe"), library.IsArtistSaved(_browseId), on => library.SetArtistSaved(artist, on));
            if (menu is not null)
            {
                // «Перемешать» в шапке кнопкой не помещается — первым пунктом «…»
                var shuffle = new MenuFlyoutItem { Text = Loc.Get("Shuffle"), Icon = new FontIcon { Glyph = "" } };
                shuffle.Click += async (_, _) => actions.PlayShuffled(await Songs());
                menu.Items.Insert(0, shuffle);
                _hero.AddMenu(menu);
            }
            _barName.Text = page.Name;
            _barPlay.Visibility = _play is null ? Visibility.Collapsed : Visibility.Visible;
        }
        Fit(ActualWidth);

        _shelves.Children.Clear();
        foreach (var shelf in page.Shelves)
        {
            var isTop = topTracks.Count > 0 && ReferenceEquals(shelf.Tracks.FirstOrDefault(), topTracks.FirstOrDefault());
            Action? more = isTop && page.SongsPlaylistId is { } id ? () => App.Services.GetRequiredService<Navigator>().Open(typeof(PlaylistPage), id) : null;
            _shelves.Children.Add(new ShelfView(shelf, new TrackContext.List(), page.IsChannel ? 50 : 5, more, columns: isTop && !page.IsChannel));
        }
    }

    /// <summary>Страница исполнителя из кэша каталога — её же берёт «Слушать» у лучшего результата поиска.</summary>
    internal static Task<ArtistDetails> DetailsAsync(string browseId, CancellationToken ct = default) =>
        App.Services.GetRequiredService<CatalogCache>().GetAsync("artist:" + browseId, () => App.Services.GetRequiredService<YouTubeMusic>().ArtistAsync(browseId, ct));

    private static List<Track> TopTracks(ArtistDetails page) =>
        page.Shelves.FirstOrDefault(s => s.Items.Count > 0 && s.Items.All(i => i is Track))?.Tracks.ToList() ?? [];

    /// <summary>«Слушать» исполнителя: все его песни, не вышло — популярные треки со страницы.</summary>
    internal static async Task<IReadOnlyList<Track>> SongsAsync(ArtistDetails page)
    {
        if (page.SongsPlaylistId is { } songs)
        {
            try
            {
                return await App.Services.GetRequiredService<YouTubeMusic>().PlaylistTracksAsync(songs, 500);
            }
            catch (YouTubeException e)
            {
                Log.Warn("Artist songs failed", e);
            }
        }
        return TopTracks(page);
    }
}
