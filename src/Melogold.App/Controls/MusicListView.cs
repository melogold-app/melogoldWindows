using System.Collections.ObjectModel;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace Melogold.App.Controls;

/// <summary>
/// Список строк (§5.3): одиночный клик выделяет, двойной клик или Enter играет — как принято в Windows; альбом,
/// исполнитель и плейлист открываются одним кликом, как ссылки. Правый клик, Shift+F10 и «…» — меню трека.
/// Отмечает ♡ и текущий трек по ходу изменений.
/// </summary>
public sealed partial class MusicListView : ListView
{
    private static readonly DataTemplate RowTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:controls="using:Melogold.App.Controls">
            <controls:MusicRow Item="{Binding}" />
        </DataTemplate>
        """);

    private static readonly DataTemplate SectionTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:controls="using:Melogold.App.Controls">
            <controls:SectionHeader Section="{Binding}" />
        </DataTemplate>
        """);

    private sealed partial class Selector : DataTemplateSelector
    {
        protected override DataTemplate SelectTemplateCore(object item) => item is SectionVm ? SectionTemplate : RowTemplate;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
    }

    public MusicListView()
    {
        ItemTemplateSelector = new Selector();
        ContainerContentChanging += (_, e) =>
        {
            // Заголовок секции не выделяется и не получает фокус как строка
            if (e.ItemContainer is ListViewItem container && e.Item is SectionVm)
            {
                container.IsTabStop = false;
                container.IsHitTestVisible = true;
                container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }
        };
        SelectionMode = ListViewSelectionMode.Single;
        IsItemClickEnabled = true;
        ItemClick += OnItemClick;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
        ContextRequested += OnContextRequested;
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>Строки и заголовки секций по порядку.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    public IEnumerable<RowVm> Rows => Entries.OfType<RowVm>();

    private static TrackActions Actions => App.Services.GetRequiredService<TrackActions>();

    /// <summary>Заменить строки списка; треки попадают в <see cref="RowOwner.Tracks"/> для «играть с этого трека».</summary>
    public void SetItems(IEnumerable<MusicItem> items, RowOwner owner, bool showType = false)
    {
        owner.Tracks.Clear();
        Entries.Clear();
        AppendItems(items, owner, showType);
        ItemsSource = Entries;
    }

    public void AppendItems(IEnumerable<MusicItem> items, RowOwner owner, bool showType = false)
    {
        var library = App.Services.GetRequiredService<Library>();
        var liked = library.LikedIds();
        var current = App.Services.GetRequiredService<PlayerEngine>().Current?.VideoId;
        foreach (var item in items)
        {
            var row = new RowVm(item, owner, showType);
            if (item is Track track)
            {
                owner.Tracks.Add(track);
                row.IsLiked = liked.Contains(track.VideoId);
                row.IsCurrent = track.VideoId == current;
            }
            Entries.Add(row);
        }
        ItemsSource ??= Entries;
    }

    public void AddSection(string title, Action? more = null)
    {
        Entries.Add(new SectionVm(title, more));
        ItemsSource ??= Entries;
    }

    public void Clear()
    {
        Entries.Clear();
        ItemsSource ??= Entries;
    }

    /// <summary>Строку нажали (двойной клик, Enter, play на обложке): трек играет по правилу списка, коллекция открывается.</summary>
    public static void Activate(RowVm row)
    {
        switch (row.Item)
        {
            case Track track:
                var index = row.Owner.Tracks.IndexOf(track);
                if (row.Owner.Context.PlaysSingle || index < 0) Actions.Play([track], 0, new TrackContext.Single());
                else Actions.Play(row.Owner.Tracks, index, row.Owner.Context);
                break;
            default:
                Open(row.Item);
                break;
        }
    }

    /// <summary>Открыть коллекцию в стеке текущего раздела.</summary>
    public static void Open(MusicItem item)
    {
        var navigator = App.Services.GetRequiredService<Navigator>();
        switch (item)
        {
            case AlbumItem album:
                navigator.Open(typeof(AlbumPage), album.BrowseId);
                break;
            case ArtistItem artist:
                navigator.Open(typeof(ArtistPage), artist.BrowseId);
                break;
            case PlaylistItem playlist:
                navigator.Open(typeof(PlaylistPage), playlist.PlaylistId);
                break;
            case MoodItem mood:
                navigator.Open(typeof(BrowsePage), mood);
                break;
        }
    }

    public static void ShowMenu(RowVm row, FrameworkElement target, Windows.Foundation.Point? at)
    {
        if (row.Track is not { } track) return;
        Action? remove = row.Owner.Remove is { } handler ? () => handler(row) : null;
        Actions.ShowMenu(track, row.Owner.Context, target, at, remove);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // Коллекции — как ссылки, одним кликом; треки одиночный клик только выделяет
        if (e.ClickedItem is RowVm { IsTrack: false } row) Open(row.Item);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RowVm row && row.IsTrack)
        {
            Activate(row);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SelectedItem is RowVm row)
        {
            Activate(row);
            e.Handled = true;
        }
        // Delete — «Убрать из плейлиста» или «Убрать из истории», где это есть в меню трека
        else if (e.Key == VirtualKey.Delete && SelectedItem is RowVm { Owner.Remove: { } remove } selected)
        {
            remove(selected);
            e.Handled = true;
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        var row = element?.DataContext as RowVm ?? SelectedItem as RowVm;
        if (row is null || element is null) return;
        ShowMenu(row, element, args.TryGetPosition(element, out var point) ? point : null);
        args.Handled = true;
    }

    // ---------- ♡ и текущий трек ----------

    private void Subscribe()
    {
        App.Services.GetRequiredService<Library>().Changed += OnLibraryChanged;
        App.Services.GetRequiredService<PlayerEngine>().TrackChanged += OnTrackChanged;
    }

    private void Unsubscribe()
    {
        App.Services.GetRequiredService<Library>().Changed -= OnLibraryChanged;
        App.Services.GetRequiredService<PlayerEngine>().TrackChanged -= OnTrackChanged;
    }

    private void OnLibraryChanged(LibraryChange change)
    {
        if (!change.HasFlag(LibraryChange.Likes)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var liked = App.Services.GetRequiredService<Library>().LikedIds();
            foreach (var row in Rows)
            {
                if (row.Track is { } track) row.IsLiked = liked.Contains(track.VideoId);
            }
        });
    }

    private void OnTrackChanged()
    {
        var current = App.Services.GetRequiredService<PlayerEngine>().Current?.VideoId;
        foreach (var row in Rows) row.IsCurrent = row.Track?.VideoId == current;
    }
}
