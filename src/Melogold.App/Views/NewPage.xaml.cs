using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

/// <summary>
/// «Новое» (REWRITE §3.4): новые альбомы и синглы («Все ›» — все новые релизы), «Для вас» со «Слушать всё», похожие
/// исполнители и альбомы, новые клипы. Без истории «Для вас» берёт рекомендации главной YouTube Music.
/// </summary>
public sealed partial class NewPage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly StateView _state = new();
    private readonly StackPanel _shelves = new();

    public NewPage()
    {
        InitializeComponent();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Loc.Get("NewHeader"), Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Top };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(refresh, Loc.Get("Refresh"));
        ToolTipService.SetToolTip(refresh, Loc.Get("Refresh"));
        refresh.Click += (_, _) => _ = RunAsync(_state, ct => LoadAsync(true, ct));
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        _content.Children.Add(header);
        _content.Children.Add(_state);
        _content.Children.Add(_shelves);
        _scroller.Content = _content;
        Content = _scroller;
        Loaded += (_, _) =>
        {
            if (_shelves.Children.Count == 0) _ = RunAsync(_state, ct => LoadAsync(false, ct));
        };
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async Task LoadAsync(bool refresh, CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var cache = App.Services.GetRequiredService<CatalogCache>();
        if (refresh)
        {
            cache.Forget("explore");
            cache.Forget("home");
        }
        var exploreTask = cache.GetAsync("explore", () => music.ExploreAsync(ct));
        var forYouTask = App.Services.GetRequiredService<ForYouBuilder>().GetAsync(refresh, ct);
        var explore = await exploreTask;
        ForYou? forYou = null;
        try
        {
            forYou = await forYouTask;
        }
        catch (Exception e) when (e is YouTubeException or HttpRequestException)
        {
            Log.Warn("For you failed", e);
        }
        ct.ThrowIfCancellationRequested();

        _shelves.Children.Clear();
        var releases = explore.FirstOrDefault(s => s.MoreBrowseId == "FEmusic_new_releases_albums") ?? explore.FirstOrDefault(s => s.Items.All(i => i is AlbumItem) && s.Items.Count > 0);
        if (releases is not null) _shelves.Children.Add(new ShelfView(releases with { Title = Loc.Get("NewReleases") }, new TrackContext.Single()));

        if (forYou is { IsEmpty: false })
        {
            var tracks = new Shelf(Loc.Get("ForYou"), forYou.Tracks);
            _shelves.Children.Add(new ShelfView(tracks, new TrackContext.List(), maxRows: 10,
                onMore: () => App.Services.GetRequiredService<TrackActions>().Play(forYou.Tracks, 0, new TrackContext.List()), moreLabel: Loc.Get("PlayAllForYou")));
            if (forYou.Artists.Count > 0) _shelves.Children.Add(new ShelfView(new Shelf(Loc.Get("SimilarArtists"), forYou.Artists), new TrackContext.Single()));
            if (forYou.Albums.Count > 0) _shelves.Children.Add(new ShelfView(new Shelf(Loc.Get("SimilarAlbums"), forYou.Albums), new TrackContext.Single()));
            if (forYou.Playlists.Count > 0) _shelves.Children.Add(new ShelfView(new Shelf(Loc.Get("PlaylistsForYou"), forYou.Playlists), new TrackContext.Single()));
        }
        else
        {
            // Новичок: истории ещё нет — рекомендации главной YouTube Music
            var home = await cache.GetAsync("home", () => music.HomeAsync(ct));
            foreach (var shelf in home.Take(4)) _shelves.Children.Add(new ShelfView(shelf, new TrackContext.List(), maxRows: 10));
        }

        var videos = explore.FirstOrDefault(s => s.MoreBrowseId == "FEmusic_new_releases_videos");
        if (videos is not null) _shelves.Children.Add(new ShelfView(videos, new TrackContext.Single()));
    }
}
