using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// Исполнитель YouTube Music одной прокруткой (REWRITE §3.7.1) или канал YouTube (§3.7.2), если музыкального профиля нет.
/// </summary>
public sealed partial class ArtistPage : CatalogPage
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36) };
    private readonly CollectionHeader _header = new();
    private readonly StateView _state = new();
    private readonly StackPanel _shelves = new();
    private string _browseId = "";

    public ArtistPage()
    {
        InitializeComponent();
        _content.Children.Add(_header);
        _content.Children.Add(_state);
        _content.Children.Add(_shelves);
        _header.Visibility = Visibility.Collapsed;
        _scroller.Content = _content;
        Content = _scroller;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _browseId = Navigator.Resolve(e.Parameter) as string ?? "";
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        var page = await DetailsAsync(_browseId, ct);
        ct.ThrowIfCancellationRequested();
        var library = App.Services.GetRequiredService<Library>();
        var actions = App.Services.GetRequiredService<TrackActions>();
        var topTracks = TopTracks(page);
        Task<IReadOnlyList<Track>> Songs() => SongsAsync(page);

        _header.Set(page.Name, page.IsChannel ? Loc.Get("YouTubeChannel") : null, page.SubscribersText, Thumbnails.Sized(page.ThumbnailUrl, 400), round: true, description: page.Description);
        if (topTracks.Count > 0)
        {
            _header.AddButton(Loc.Get("PlayAll"), "", async () => actions.Play(await Songs(), 0, new TrackContext.List()), accent: true);
            _header.AddButton(Loc.Get("Shuffle"), "", async () => actions.PlayShuffled(await Songs()));
        }
        var artist = new ArtistItem { BrowseId = _browseId, Name = page.Name, ThumbnailUrl = page.ThumbnailUrl, IsChannel = page.IsChannel };
        _header.AddToggle(on => Loc.Get(on ? "Subscribed" : "Subscribe"), library.IsArtistSaved(_browseId), on => library.SetArtistSaved(artist, on));
        if (topTracks.Count > 0)
        {
            _header.AddMenu(App.Services.GetRequiredService<CollectionMenu>().Build(Songs,
                new ShareTarget(page.Name, null, ShareLinks.Artist(_browseId, page.IsChannel))));
        }
        _header.Visibility = Visibility.Visible;

        _shelves.Children.Clear();
        foreach (var shelf in page.Shelves)
        {
            var isTop = ReferenceEquals(shelf.Tracks.FirstOrDefault(), topTracks.FirstOrDefault()) && topTracks.Count > 0;
            Action? more = isTop && page.SongsPlaylistId is { } id ? () => App.Services.GetRequiredService<Navigator>().Open(typeof(PlaylistPage), id) : null;
            _shelves.Children.Add(new ShelfView(shelf, new TrackContext.List(), page.IsChannel ? 50 : 5, more));
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
