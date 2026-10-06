using Melogold.App.Services;
using Melogold.App.Views;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Лучший результат поиска (tasks/0023, доктрина §4.6) — крупная карточка над выдачей. Исполнитель: фото кругом, имя,
/// «Исполнитель», «Слушать» (все его песни) и «Открыть»; альбом: обложка, название, исполнитель, «Слушать» и «Открыть»;
/// трек: обложка, название, исполнитель. Нажатие на карточку открывает исполнителя или альбом, трек — играет; правый
/// клик по треку — его меню. Вся карточка — одна кнопка (наведение, фокус, Enter, диктор), кнопки действий поверх неё.
/// </summary>
public sealed partial class TopResultCard : Grid
{
    private const double ArtworkSize = 112;
    private readonly MusicItem _item;

    public TopResultCard(MusicItem item)
    {
        _item = item;
        RowSpacing = 8;
        Margin = new Thickness(0, 16, 0, 8);
        // Карточка не шире 560: в широком окне не растягивается на весь экран, в узком — во всю ширину
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 560 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock { Text = Loc.Get("ResultsTopResult"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        Children.Add(heading);

        var (title, kind, subtitle, imageUrl, round) = item switch
        {
            ArtistItem artist => (artist.Name, Loc.Get(artist.IsChannel ? "TypeChannel" : "TypeArtist"), artist.Subtitle, artist.ThumbnailUrl, true),
            AlbumItem album => (album.Title, album.TypeText ?? Loc.Get("TypeAlbum"), Join(album.ArtistsText, album.Year), album.ThumbnailUrl, false),
            Track track => (track.Title, Loc.Get(track.IsVideo ? "TypeVideo" : "TypeSong"), Join(track.ArtistsText, track.AlbumTitle),
                track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), false),
            PlaylistItem playlist => (playlist.Title, Loc.Get("ResultsPlaylists"), playlist.Subtitle, playlist.ThumbnailUrl, false),
            _ => (item.ToString() ?? "", "", null, null, false),
        };

        var card = new Grid();
        SetRow(card, 1);
        Children.Add(card);

        // Нижний слой — сама карточка-кнопка: фон, рамка, наведение и нажатие как у системной кнопки
        var surface = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            // Элемент внутри страницы — скругление элементов управления (4), не окон и всплывающих (8)
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
        };
        AutomationProperties.SetName(surface, $"{Loc.Get("ResultsTopResult")}: {title}, {kind}");
        surface.Click += (_, _) => Activate();
        if (item is Track menuTrack)
        {
            surface.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                var at = e.TryGetPosition(surface, out var point) ? point : (Windows.Foundation.Point?)null;
                Actions.ShowMenu(menuTrack, new TrackContext.Single(), surface, at);
            };
        }
        card.Children.Add(surface);

        var content = new Grid { Padding = new Thickness(16), ColumnSpacing = 16 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(content);

        var artwork = new Border
        {
            Width = ArtworkSize,
            Height = ArtworkSize,
            CornerRadius = new CornerRadius(round ? ArtworkSize / 2 : 6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Image { Source = Images.From(Thumbnails.Sized(imageUrl, 240), 240), Stretch = Stretch.UniformToFill },
        };
        content.Children.Add(artwork);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(text, 1);
        content.Children.Add(text);
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        });
        text.Children.Add(new TextBlock { Text = kind, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock { Text = subtitle, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });

        if (item is ArtistItem or AlbumItem)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
            text.Children.Add(buttons);
            var listen = Button(Loc.Get("PlayAll"), "");
            listen.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            listen.Click += async (_, _) =>
            {
                listen.IsEnabled = false;
                try { await ListenAsync(); }
                finally { listen.IsEnabled = true; }
            };
            buttons.Children.Add(listen);
            var open = Button(Loc.Get("ResultsOpen"), null);
            open.Click += (_, _) => MusicListView.Open(item);
            buttons.Children.Add(open);
        }
    }

    private static TrackActions Actions => App.Services.GetRequiredService<TrackActions>();

    private static Button Button(string label, string? glyph)
    {
        if (glyph is null) return new Button { Content = label };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = label });
        return new Button { Content = content };
    }

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length > 0 ? text : null;
    }

    /// <summary>Одно нажатие: исполнитель и альбом открываются, трек играет.</summary>
    private void Activate()
    {
        if (_item is Track track) Actions.Play([track], 0, new TrackContext.Single());
        else MusicListView.Open(_item);
    }

    private async Task ListenAsync()
    {
        try
        {
            IReadOnlyList<Track> tracks = _item switch
            {
                ArtistItem artist => await ArtistPage.SongsAsync(await ArtistPage.DetailsAsync(artist.BrowseId)),
                AlbumItem album => (await App.Services.GetRequiredService<CatalogCache>().GetAsync("album:" + album.BrowseId,
                    () => App.Services.GetRequiredService<YouTubeMusic>().AlbumAsync(album.BrowseId))).Tracks,
                _ => [],
            };
            if (tracks.Count > 0) Actions.Play(tracks, 0, new TrackContext.List());
            else MusicListView.Open(_item);
        }
        catch (Exception e) when (e is YouTubeException or HttpRequestException)
        {
            Log.Warn("Top result playback failed", e);
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Get(e is YouTubeException { Kind: YouTubeErrorKind.Blocked } ? "ErrorBlocked" : "ErrorOffline"));
        }
    }
}
