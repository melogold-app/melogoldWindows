using System.Globalization;
using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>
/// «Плейлист по ссылке» (tasks/0016, API §4.11): снимок чужого или своего плейлиста с любого сервера Melogold, без входа.
/// «Слушать», «Перемешать» и «Сохранить в Библиотеку» — свой плейлист с этими треками и их метаданными; сохранять —
/// только кнопкой (API §7.2). Удалённый или неверный — «Ссылка удалена или неверна».
/// </summary>
public sealed partial class SharedPlaylistPage : CatalogPage
{
    private readonly MusicListView _list = new() { Padding = new Thickness(36, 24, 36, 24) };
    private readonly CollectionHeader _header = new();
    private readonly StateView _state = new();
    private ShareRef? _share;

    public SharedPlaylistPage()
    {
        InitializeComponent();
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(_state);
        _list.Header = top;
        _header.Visibility = Visibility.Collapsed;
        Content = _list;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _share = Navigator.Resolve(e.Parameter) as ShareRef;
        _ = RunAsync(_state, LoadAsync);
    }

    public override void ScrollToTop() => SearchPage.FindScrollViewer(_list)?.ChangeView(null, 0, null);

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_share is not { } share) return;
        ShareDto snapshot;
        try
        {
            snapshot = await App.Services.GetRequiredService<AccountService>().OpenShareAsync(share, ct);
        }
        catch (ApiException e) when (e.Status is 404 or 400)
        {
            _state.ShowEmpty("", Loc.Get("ShareGone"));
            throw new StateShownException();
        }
        catch (ApiException e) when (e.IsNetwork)
        {
            throw new HttpRequestException(e.Message, e);
        }
        ct.ThrowIfCancellationRequested();
        var tracks = snapshot.Tracks.Select(TrackDtos.ToTrackOrStub).ToList();
        var actions = App.Services.GetRequiredService<TrackActions>();
        var host = Uri.TryCreate(share.ServerUrl, UriKind.Absolute, out var server) ? server.Host : share.ServerUrl;
        _header.Set(snapshot.Name, $"{Loc.Get("SharedPlaylist")} · {host}", Loc.Plural("Tracks", tracks.Count),
            Thumbnails.Sized(tracks.FirstOrDefault(t => t.ThumbnailUrl is not null)?.ThumbnailUrl, 400));
        _header.ClearButtons();
        _header.AddButton(Loc.Get("PlayAll"), "", () => actions.Play(tracks, 0, new TrackContext.List()), accent: true);
        _header.AddButton(Loc.Get("Shuffle"), "", () => actions.PlayShuffled(tracks));
        _header.AddButton(Loc.Get("ShareSaveToLibrary"), "", () => Save(snapshot.Name, tracks));
        _header.Visibility = Visibility.Visible;
        _list.SetItems(tracks, new RowOwner(new TrackContext.List()));
        if (tracks.Count == 0) _state.ShowEmpty("", Loc.Get("PlaylistEmpty"));
    }

    private static void Save(string name, IReadOnlyList<Track> tracks)
    {
        var id = App.Services.GetRequiredService<Library>().CreatePlaylist(name, tracks);
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("ShareSavedToLibrary"), Loc.Get("SaveFileOpen"),
            () => App.Services.GetRequiredService<Navigator>().Open(typeof(LocalPlaylistPage), id.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// «Мои ссылки» (Аккаунт, tasks/0016, API §4.11): снимки своих плейлистов — название, треков, дата; «Скопировать ссылку»
/// и «Удалить ссылку» — после удаления ссылка перестаёт открываться.
/// </summary>
public sealed partial class MySharesPage : Page
{
    private readonly AccountService _account = Form.Account;
    private readonly StackPanel _list = new() { Spacing = (double)Application.Current.Resources["SettingsCardSpacing"] };
    private readonly StateView _state = new();

    public MySharesPage()
    {
        InitializeComponent();
        var (scroller, body) = Form.Page(Loc.Get("MyShares"), Loc.Get("MySharesText"));
        body.Children.Add(_state);
        body.Children.Add(_list);
        Content = scroller;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _state.ShowLoading();
        ShareList list;
        try
        {
            list = await _account.SharesAsync();
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Log.Warn("Shares not loaded", e);
            _state.ShowError(e is ApiException { IsNetwork: true } ? new HttpRequestException(e.Message, e) : e, () => _ = LoadAsync());
            return;
        }
        _list.Children.Clear();
        if (list.Shares.Count == 0)
        {
            _state.ShowEmpty("", Loc.Get("MySharesEmpty"), Loc.Get("MySharesEmptyHint"));
            return;
        }
        _state.ShowContent();
        foreach (var share in list.Shares) _list.Children.Add(Card(share));
    }

    private CommunityToolkit.WinUI.Controls.SettingsCard Card(ShareDto share)
    {
        var date = IsoTime.TryParse(share.CreatedAt) is { } at
            ? DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime.ToString("d", CultureInfo.CurrentCulture)
            : "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = Loc.Get("MenuCopyLink") };
        copy.Click += (_, _) => Share.CopyLink(share.Url);
        buttons.Children.Add(copy);
        var delete = new Button { Content = Loc.Get("ShareDelete") };
        delete.Click += async (_, _) => await DeleteAsync(share);
        buttons.Children.Add(delete);
        return new CommunityToolkit.WinUI.Controls.SettingsCard
        {
            Header = share.Name,
            Description = string.Join(" · ", new[] { Loc.Plural("Tracks", share.Tracks.Count), date }.Where(s => s.Length > 0)),
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = buttons,
        };
    }

    private async Task DeleteAsync(ShareDto share)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("ShareDeleteTitleFormat", share.Name),
            Content = new TextBlock { Text = Loc.Get("ShareDeleteText"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("ShareDelete"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _account.DeleteShareAsync(share.ShareId);
        }
        catch (ApiException e) when (e.Status == 404)
        {
            // Уже удалена — на другом устройстве
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Log.Warn("Share not deleted", e);
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Get(e is ApiException { IsNetwork: true } ? "ErrorOffline" : "ErrorUnknown"));
            return;
        }
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("ShareDeleted"));
        await LoadAsync();
    }
}
