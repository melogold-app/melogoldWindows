using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Music;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Полка страницы (§5.4): заголовок с «Все ›», внутри — строки треков (до <see cref="MaxRows"/>) или горизонтальный ряд
/// карточек; настроения — плитки с цветной полоской.
/// </summary>
public sealed partial class ShelfView : StackPanel
{
    private static readonly DataTemplate CardTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:controls="using:Melogold.App.Controls">
            <controls:MediaCard Card="{Binding}" />
        </DataTemplate>
        """);

    public ShelfView(Shelf shelf, TrackContext context, int maxRows = 5, Action? onMore = null, string? moreLabel = null)
    {
        Spacing = 4;
        MaxRows = maxRows;
        var header = new Grid { Margin = new Thickness(0, 24, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrEmpty(shelf.Title))
        {
            var title = new TextBlock { Text = shelf.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            header.Children.Add(title);
        }
        var more = onMore ?? MoreAction(shelf);
        var tracks = shelf.Items.OfType<Track>().ToList();
        if (more is null && tracks.Count > maxRows && tracks.Count == shelf.Items.Count)
            more = () => App.Services.GetRequiredService<Navigator>().Open(typeof(TrackListPage), new TrackListRequest(shelf.Title ?? "", tracks));
        if (more is not null)
        {
            var button = new HyperlinkButton { Content = moreLabel ?? Loc.Get("SeeAll") + " ›", VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => more();
            Grid.SetColumn(button, 1);
            header.Children.Add(button);
        }
        if (header.Children.Count > 0) Children.Add(header);

        if (shelf.Items.All(i => i is Track) && shelf.Items.Count > 0 && !IsVideoCarousel(shelf))
        {
            var list = new MusicListView();
            var owner = new RowOwner(context);
            list.SetItems(shelf.Items.Take(maxRows), owner);
            // Весь список полки играет с выбранного трека, даже если видны не все строки
            owner.Tracks.Clear();
            owner.Tracks.AddRange(tracks);
            Children.Add(list);
        }
        else if (shelf.Items.All(i => i is MoodItem))
        {
            Children.Add(MoodGrid(shelf.Items.OfType<MoodItem>()));
        }
        else
        {
            Children.Add(CardRow(shelf.Items));
        }
    }

    public int MaxRows { get; }

    /// <summary>Клипы и видео YTM приходят карточками 16:9 — показываем их рядом, а не строками.</summary>
    private static bool IsVideoCarousel(Shelf shelf) => shelf.Items.OfType<Track>().All(t => t.IsVideo) && shelf.Items.Count > 0 && shelf.Items.OfType<Track>().All(t => t.DurationText is null);

    private static Action? MoreAction(Shelf shelf)
    {
        if (shelf.MoreBrowseId is not { } browseId) return null;
        var navigator = App.Services.GetRequiredService<Navigator>();
        if (browseId.StartsWith("VL", StringComparison.Ordinal)) return () => navigator.Open(typeof(PlaylistPage), browseId[2..]);
        if (browseId == "FEmusic_moods_and_genres") return () => navigator.Open(typeof(BrowsePage), new BrowseRequest(shelf.Title ?? "", browseId, null));
        if (browseId == "FEmusic_new_releases_albums") return () => navigator.Open(typeof(BrowsePage), new BrowseRequest(shelf.Title ?? "", browseId, null));
        if (browseId.StartsWith("UC", StringComparison.Ordinal) && shelf.MoreParams is null) return () => navigator.Open(typeof(ArtistPage), browseId);
        return () => navigator.Open(typeof(BrowsePage), new BrowseRequest(shelf.Title ?? "", browseId, shelf.MoreParams));
    }

    /// <summary>Горизонтальный ряд карточек.</summary>
    public static ListView CardRow(IEnumerable<MusicItem> items)
    {
        var list = new ListView
        {
            ItemTemplate = CardTemplate,
            ItemsSource = items.Select(i => new CardVm(i)).ToList(),
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(
                """
                <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ItemsStackPanel Orientation="Horizontal" />
                </ItemsPanelTemplate>
                """),
            Padding = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemClick += (_, e) => OpenCard((CardVm)e.ClickedItem);
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer is ListViewItem item) item.Padding = new Thickness(0, 0, 16, 8);
        };
        return list;
    }

    /// <summary>Сетка карточек (страницы «Все»).</summary>
    public static GridView CardGrid(IEnumerable<MusicItem> items)
    {
        var grid = new GridView
        {
            ItemTemplate = CardTemplate,
            ItemsSource = items.Select(i => new CardVm(i)).ToList(),
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
        };
        grid.ItemClick += (_, e) => OpenCard((CardVm)e.ClickedItem);
        return grid;
    }

    private static void OpenCard(CardVm card)
    {
        if (card.Item is Track track)
        {
            App.Services.GetRequiredService<TrackActions>().Play([track], 0, new TrackContext.Single());
            return;
        }
        MusicListView.Open(card.Item);
    }

    /// <summary>Плитки «Настроения и жанры»: кнопки с цветной полоской слева.</summary>
    public static FrameworkElement MoodGrid(IEnumerable<MoodItem> moods)
    {
        var panel = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 208, ItemHeight = 56 };
        foreach (var mood in moods)
        {
            var color = mood.Color ?? 0xFF808080;
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
            });
            var label = new TextBlock { Text = mood.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            var button = new Button
            {
                Content = content,
                Width = 200,
                Height = 48,
                Padding = new Thickness(8, 6, 12, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) => MusicListView.Open(mood);
            panel.Children.Add(button);
        }
        return panel;
    }
}
