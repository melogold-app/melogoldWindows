using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// Страница полок YouTube Music (REWRITE §3.9): настроение, «Все настроения», «Все новые релизы», «Все» полки
/// исполнителя. Одна полка карточек показывается сеткой.
/// </summary>
public sealed partial class BrowsePage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly StateView _state = new();
    private readonly StackPanel _shelves = new();
    private BrowseRequest _request = new("", "", null);

    public BrowsePage()
    {
        InitializeComponent();
        _content.Children.Add(_title);
        _content.Children.Add(_state);
        _content.Children.Add(_shelves);
        _scroller.Content = _content;
        Content = _scroller;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = Navigator.Resolve(e.Parameter) switch
        {
            BrowseRequest r => r,
            MoodItem mood => new BrowseRequest(mood.Title, mood.BrowseId, mood.Params),
            _ => _request,
        };
        _title.Text = _request.Title;
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var shelves = await App.Services.GetRequiredService<CatalogCache>().GetAsync($"browse:{_request.BrowseId}:{_request.Params}",
            () => music.BrowseShelvesAsync(_request.BrowseId, _request.Params, ct));
        ct.ThrowIfCancellationRequested();
        _shelves.Children.Clear();
        if (shelves.Count == 1 && shelves[0].Items.All(i => i is not Track))
        {
            var shelf = shelves[0];
            if (!string.IsNullOrEmpty(shelf.Title) && shelf.Title != _request.Title)
                _shelves.Children.Add(new TextBlock { Text = shelf.Title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
            _shelves.Children.Add(shelf.Items.All(i => i is MoodItem) ? ShelfView.MoodGrid(shelf.Items.OfType<MoodItem>()) : ShelfView.CardGrid(shelf.Items));
            return;
        }
        foreach (var shelf in shelves) _shelves.Children.Add(new ShelfView(shelf, new TrackContext.Single()));
    }
}

/// <summary>Все треки полки («Все ›»): список, который играет целиком.</summary>
public sealed partial class TrackListPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["PageTitleStyle"] };

    public TrackListPage()
    {
        InitializeComponent();
        _list.Header = _title;
        Content = _list;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (Navigator.Resolve(e.Parameter) is not TrackListRequest request) return;
        _title.Text = request.Title;
        _list.SetItems(request.Tracks, new RowOwner(new TrackContext.List()));
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);
}
