using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

/// <summary>
/// «Тренды» (REWRITE §3.3): «В тренде» — чарт из обзора YouTube Music, «Весь список» — плейлист чарта; «Настроения и
/// жанры» — плитки, «Все ›» — все настроения. Стартовый раздел при первом запуске.
/// </summary>
public sealed partial class TrendsPage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly StateView _state = new();
    private readonly StackPanel _shelves = new();

    public TrendsPage()
    {
        InitializeComponent();
        var title = new TextBlock { Text = Loc.Get("TrendsHeader"), Style = (Style)Application.Current.Resources["PageTitleStyle"] };
        _content.Children.Add(title);
        _content.Children.Add(_state);
        _content.Children.Add(_shelves);
        _scroller.Content = _content;
        Content = _scroller;
        Loaded += (_, _) =>
        {
            if (_shelves.Children.Count == 0) _ = RunAsync(_state, LoadAsync);
        };
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var explore = await App.Services.GetRequiredService<CatalogCache>().GetAsync("explore", () => music.ExploreAsync(ct));
        ct.ThrowIfCancellationRequested();
        _shelves.Children.Clear();
        // В тренде: треки обзора играют списком
        foreach (var shelf in explore.Where(s => s.Items.Count > 0 && s.Items.All(i => i is Track) && s.Tracks.Any(t => !t.IsVideo || t.DurationText is not null)))
            _shelves.Children.Add(new ShelfView(shelf, new TrackContext.List(), maxRows: 10));
        foreach (var shelf in explore.Where(s => s.Items.Count > 0 && s.Items.All(i => i is MoodItem)))
            _shelves.Children.Add(new ShelfView(shelf, new TrackContext.List()));
    }
}
