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

/// <summary>Альбом (REWRITE §3.6): шапка, треки, «Другие версии» и похожие релизы ниже.</summary>
public sealed partial class AlbumPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly CollectionHeader _header = new();
    private readonly StateView _state = new();
    private readonly StackPanel _footer = new();
    private string _browseId = "";

    public AlbumPage()
    {
        InitializeComponent();
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_state);
        _list.Header = top;
        _list.Footer = _footer;
        _header.Visibility = Visibility.Collapsed;
        Content = _list;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _browseId = Navigator.Resolve(e.Parameter) as string ?? "";
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        var music = App.Services.GetRequiredService<YouTubeMusic>();
        var page = await App.Services.GetRequiredService<CatalogCache>().GetAsync("album:" + _browseId, () => music.AlbumAsync(_browseId, ct));
        ct.ThrowIfCancellationRequested();
        var album = page.Album;
        var library = App.Services.GetRequiredService<Library>();
        var actions = App.Services.GetRequiredService<TrackActions>();

        _header.Set(album.Title, string.Join(" · ", new[] { album.TypeText, album.ArtistsText, album.Year }.Where(s => !string.IsNullOrEmpty(s))),
            page.CountText, Thumbnails.Sized(album.ThumbnailUrl, 400), description: page.Description);
        _header.AddButton(Loc.Get("PlayAll"), "", () => actions.Play(page.Tracks, 0, new TrackContext.List()), accent: true);
        _header.AddButton(Loc.Get("Shuffle"), "", () => actions.PlayShuffled(page.Tracks));
        _header.AddToggle(on => Loc.Get(on ? "InLibraryCheck" : "SaveToLibrary"), library.IsAlbumSaved(album.BrowseId), on => library.SetAlbumSaved(album, on));
        _header.AddMenu(App.Services.GetRequiredService<CollectionMenu>().Build(() => Task.FromResult(page.Tracks),
            album.PlaylistId is { } playlist ? $"https://music.youtube.com/playlist?list={playlist}" : $"https://music.youtube.com/browse/{album.BrowseId}"));
        _header.Visibility = Visibility.Visible;

        _list.SetItems(page.Tracks, new RowOwner(new TrackContext.List()));
        _footer.Children.Clear();
        foreach (var shelf in page.Shelves) _footer.Children.Add(new ShelfView(shelf, new TrackContext.Single()));
    }
}
