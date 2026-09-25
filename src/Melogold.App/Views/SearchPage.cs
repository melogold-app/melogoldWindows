using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// Выдача поиска (§5.4, REWRITE §3.1.3): <c>SelectorBar</c> «Всё · Музыка · YouTube». «Всё» — два параллельных
/// запроса: YouTube Music (лучший результат и первые строки) и обычный YouTube (видео, которых нет в YTM). «Музыка» —
/// Песни · Альбомы · Исполнители · Клипы · Плейлисты, «YouTube» — Видео · Каналы · Трансляции · Плейлисты, с продолжениями.
/// </summary>
public sealed partial class SearchPage : CatalogPage
{
    private readonly YouTubeMusic _music = App.Services.GetRequiredService<YouTubeMusic>();
    private readonly CatalogCache _cache = App.Services.GetRequiredService<CatalogCache>();
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly SelectorBar _scopes = new();
    private readonly SelectorBar _filters = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly InfoBar _note = new() { IsClosable = false, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StateView _state = new();
    private string _query = "";
    private SearchScope _scope;
    private string? _continuation;
    private Func<string, CancellationToken, Task<ItemsPage>>? _more;
    private bool _loadingMore;
    private RowOwner _owner = new(new TrackContext.Single());

    private static readonly string[] MusicFilters = ["ResultsSongs", "ResultsAlbums", "ResultsArtists", "ResultsMusicVideos", "ResultsPlaylists"];
    private static readonly string[] YouTubeFilters = ["ResultsVideos", "ResultsChannels", "ResultsLive", "ResultsPlaylists"];

    public SearchPage()
    {
        InitializeComponent();
        foreach (var (key, scope) in new[] { ("ResultsAll", SearchScope.All), ("ResultsMusic", SearchScope.Music), ("ResultsYouTube", SearchScope.YouTube) })
            _scopes.Items.Add(new SelectorBarItem { Text = Loc.Get(key), Tag = scope });
        _scopes.SelectionChanged += (_, _) =>
        {
            if (_scopes.SelectedItem?.Tag is SearchScope scope && scope != _scope) Show(scope, 0);
        };
        _filters.SelectionChanged += (_, _) =>
        {
            if (_filters.SelectedItem?.Tag is int filter) Load(filter);
        };
        var header = new StackPanel();
        header.Children.Add(_title);
        header.Children.Add(_scopes);
        header.Children.Add(_filters);
        header.Children.Add(_note);
        header.Children.Add(_state);
        _note.IsOpen = false;
        _list.Header = header;
        _list.Loaded += (_, _) =>
        {
            if (FindScrollViewer(_list) is { } scroller) scroller.ViewChanged += (_, _) =>
            {
                if (scroller.VerticalOffset > scroller.ScrollableHeight - 800) _ = LoadMoreAsync();
            };
        };
        Content = _list;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var request = Navigator.Resolve(e.Parameter) switch
        {
            SearchRequest r => r,
            string text => new SearchRequest(text),
            _ => new SearchRequest(""),
        };
        _query = request.Query;
        _title.Text = _query;
        Show(request.Scope, 0);
    }

    public override void ScrollToTop()
    {
        if (FindScrollViewer(_list) is { } scroller) scroller.ChangeView(null, 0, null);
    }

    private void Show(SearchScope scope, int filter)
    {
        _scope = scope;
        _scopes.SelectedItem = _scopes.Items.First(i => (SearchScope)i.Tag == scope);
        _filters.Items.Clear();
        var keys = scope switch { SearchScope.Music => MusicFilters, SearchScope.YouTube => YouTubeFilters, _ => [] };
        for (var i = 0; i < keys.Length; i++) _filters.Items.Add(new SelectorBarItem { Text = Loc.Get(keys[i]), Tag = i });
        _filters.Visibility = keys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keys.Length > 0) _filters.SelectedItem = _filters.Items[filter];
        else Load(0);
    }

    private void Load(int filter)
    {
        _list.Clear();
        _note.IsOpen = false;
        _continuation = null;
        _more = null;
        _owner = new RowOwner(new TrackContext.Single());
        _ = _scope == SearchScope.All ? RunAsync(_state, LoadAllAsync) : RunAsync(_state, ct => LoadFilteredAsync(filter, ct));
    }

    private async Task LoadAllAsync(CancellationToken ct)
    {
        var query = _query;
        var ytmTask = _cache.GetAsync($"search:all:{query}", () => _music.SearchSummaryAsync(query, ct));
        var webTask = _cache.GetAsync($"search:web:{query}:0", () => _music.SearchWebAsync(query, WebSearchFilter.Videos, ct));
        SearchSummary? ytm = null;
        ItemsPage? web = null;
        Exception? error = null;
        try { ytm = await ytmTask; } catch (Exception e) when (e is not OperationCanceledException) { error = e; }
        try { web = await webTask; } catch (Exception e) when (e is not OperationCanceledException) { error ??= e; }
        ct.ThrowIfCancellationRequested();
        if (ytm is null && web is null) throw error!;

        var hidden = App.Services.GetRequiredService<Library>().HiddenTracks();
        var ytmItems = new List<MusicItem>();
        if (ytm?.TopResult is { } top) ytmItems.Add(top);
        ytmItems.AddRange((ytm?.Items ?? []).Where(i => !Same(i, ytm?.TopResult)));
        ytmItems = ytmItems.Where(i => i is not Track t || !hidden.Contains(t.VideoId)).Take(8).ToList();
        var known = ytmItems.OfType<Track>().Select(t => t.VideoId).ToHashSet();
        var videos = (web?.Items ?? []).OfType<Track>().Where(t => !known.Contains(t.VideoId) && !hidden.Contains(t.VideoId)).Take(6).ToList();

        if (ytmItems.Count == 0 && videos.Count == 0)
        {
            _state.ShowEmpty("", Loc.Get("ResultsNothing"));
            throw new StateShownException();
        }
        if (ytmItems.Count == 0)
        {
            _note.Message = Loc.Get("ResultsNothingInCatalog");
            _note.IsOpen = true;
        }
        void AddYouTube()
        {
            if (videos.Count == 0) return;
            _list.AddSection(Loc.Get("ResultsYouTube"), () => Show(SearchScope.YouTube, 0));
            _list.AppendItems(videos, _owner, showType: false);
        }
        if (ytmItems.Count == 0) AddYouTube();
        if (ytmItems.Count > 0)
        {
            _list.AddSection("YouTube Music", () => Show(SearchScope.Music, 0));
            _list.AppendItems(ytmItems, _owner, showType: true);
            AddYouTube();
        }
    }

    private static bool Same(MusicItem a, MusicItem? b) => b is not null && (a, b) switch
    {
        (Track x, Track y) => x.VideoId == y.VideoId,
        (AlbumItem x, AlbumItem y) => x.BrowseId == y.BrowseId,
        (ArtistItem x, ArtistItem y) => x.BrowseId == y.BrowseId,
        (PlaylistItem x, PlaylistItem y) => x.PlaylistId == y.PlaylistId,
        _ => false,
    };

    private async Task LoadFilteredAsync(int filter, CancellationToken ct)
    {
        var query = _query;
        ItemsPage page;
        if (_scope == SearchScope.Music)
        {
            var type = filter switch
            {
                0 => MusicSearchFilter.Songs,
                1 => MusicSearchFilter.Albums,
                2 => MusicSearchFilter.Artists,
                3 => MusicSearchFilter.Videos,
                _ => MusicSearchFilter.CommunityPlaylists,
            };
            page = await _cache.GetAsync($"search:{type}:{query}", () => _music.SearchAsync(query, type, ct));
            _more = (token, c) => _music.SearchContinuationAsync(token, c);
        }
        else
        {
            var type = filter switch
            {
                0 => WebSearchFilter.Videos,
                1 => WebSearchFilter.Channels,
                2 => WebSearchFilter.Live,
                _ => WebSearchFilter.Playlists,
            };
            page = await _cache.GetAsync($"search:web:{query}:{(int)type}", () => _music.SearchWebAsync(query, type, ct));
            _more = (token, c) => _music.SearchWebContinuationAsync(token, c);
        }
        ct.ThrowIfCancellationRequested();
        if (page.Items.Count == 0)
        {
            _state.ShowEmpty("", Loc.Get("ResultsNothing"));
            throw new StateShownException();
        }
        _list.AppendItems(page.Items, _owner);
        _continuation = page.Continuation;
    }

    /// <summary>Продолжение выдачи, когда до конца списка осталось немного.</summary>
    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _continuation is not { } token || _more is not { } more) return;
        _loadingMore = true;
        try
        {
            var page = await more(token, CancellationToken.None);
            if (token != _continuation) return;
            var known = _list.Rows.Select(r => r.Item).ToList();
            _list.AppendItems(page.Items.Where(i => !known.Any(k => Same(i, k))), _owner);
            _continuation = page.Continuation == token ? null : page.Continuation;
        }
        catch (Exception e) when (e is YouTubeException or HttpRequestException)
        {
            Log.Warn("Search continuation failed", e);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
