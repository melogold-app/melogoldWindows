using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// Плейлист YouTube, он же «Весь список» чарта (REWRITE §3.8.2): первая страница сразу, остальные догружаются
/// продолжениями до конца (плейлисты длиннее 100 треков, §8.5). «Сохранить» делает свой плейлист со связью.
/// </summary>
public sealed partial class PlaylistPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly CollectionHeader _header = new();
    private readonly StateView _state = new();
    private readonly ProgressBar _more = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    private string _playlistId = "";
    private List<Track> _tracks = [];
    private Task? _loadingRest;

    public PlaylistPage()
    {
        InitializeComponent();
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_state);
        _list.Header = top;
        _list.Footer = _more;
        _header.Visibility = Visibility.Collapsed;
        Content = _list;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _playlistId = Navigator.Resolve(e.Parameter) as string ?? "";
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var cache = App.Services.GetRequiredService<CatalogCache>();
        var page = await cache.GetAsync("playlist:" + _playlistId, () => music.PlaylistAsync(_playlistId, ct));
        ct.ThrowIfCancellationRequested();
        var library = App.Services.GetRequiredService<Library>();
        var actions = App.Services.GetRequiredService<TrackActions>();
        _tracks = page.Tracks.ToList();

        _header.Set(page.Playlist.Title, string.Join(" · ", new[] { Loc.Get("TypePlaylist"), page.AuthorText }.Where(s => !string.IsNullOrEmpty(s))),
            page.CountText, Thumbnails.Sized(page.Playlist.ThumbnailUrl, 400), description: page.Description);
        _header.AddButton(Loc.Get("PlayAll"), "", async () => actions.Play(await AllTracksAsync(), 0, new TrackContext.List()), accent: true);
        _header.AddButton(Loc.Get("Shuffle"), "", async () => actions.PlayShuffled(await AllTracksAsync()));
        _header.AddButton(Loc.Get("SaveAsPlaylist"), "", async () =>
        {
            var all = await AllTracksAsync();
            library.CreatePlaylist(page.Playlist.Title, all, page.Playlist.PlaylistId, page.Playlist.ThumbnailUrl);
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Format("SavedToLibraryFormat", page.Playlist.Title));
        });
        _header.AddMenu(App.Services.GetRequiredService<CollectionMenu>().Build(AllTracksAsync, $"https://www.youtube.com/playlist?list={page.Playlist.PlaylistId}"));
        _header.Visibility = Visibility.Visible;

        var owner = new RowOwner(new TrackContext.List());
        _list.SetItems(page.Tracks, owner);
        if (page.Continuation is { } continuation) _loadingRest = LoadRestAsync(continuation, owner, ct);
    }

    /// <summary>Продолжения — до конца плейлиста, строки дописываются по мере прихода.</summary>
    private async Task LoadRestAsync(string continuation, RowOwner owner, CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var seen = _tracks.Select(t => t.VideoId).ToHashSet();
        _more.Visibility = Visibility.Visible;
        try
        {
            string? token = continuation;
            while (token is not null && !ct.IsCancellationRequested && _tracks.Count < 10_000)
            {
                var next = await music.PlaylistContinuationAsync(token, ct);
                var fresh = next.Items.OfType<Track>().Where(t => seen.Add(t.VideoId)).ToList();
                _tracks.AddRange(fresh);
                _list.AppendItems(fresh, owner);
                token = next.Continuation == token || next.Items.Count == 0 ? null : next.Continuation;
            }
        }
        catch (Exception e) when (e is YouTubeException or OperationCanceledException)
        {
            Log.Warn("Playlist continuation stopped", e);
        }
        finally
        {
            _more.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<IReadOnlyList<Track>> AllTracksAsync()
    {
        if (_loadingRest is { } rest) await rest;
        return _tracks;
    }
}
