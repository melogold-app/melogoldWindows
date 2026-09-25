using Melogold.App.Services;
using Melogold.Core.Music;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>Данные карточки: альбом, исполнитель, плейлист, клип или настроение.</summary>
public sealed class CardVm
{
    public CardVm(MusicItem item)
    {
        Item = item;
        switch (item)
        {
            case AlbumItem album:
                Title = album.Title;
                Subtitle = string.Join(" · ", new[] { album.TypeText, album.Year ?? album.ArtistsText }.Where(s => !string.IsNullOrEmpty(s)));
                ImageUrl = Thumbnails.Sized(album.ThumbnailUrl, 320);
                break;
            case ArtistItem artist:
                Title = artist.Name;
                Subtitle = artist.Subtitle ?? Loc.Get(artist.IsChannel ? "TypeChannel" : "TypeArtist");
                ImageUrl = Thumbnails.Sized(artist.ThumbnailUrl, 320);
                IsRound = true;
                break;
            case PlaylistItem playlist:
                Title = playlist.Title;
                Subtitle = playlist.Subtitle ?? Loc.Get("TypePlaylist");
                ImageUrl = Thumbnails.Sized(playlist.ThumbnailUrl, 320);
                break;
            case Track track:
                Title = track.Title;
                Subtitle = track.ArtistsText ?? "";
                ImageUrl = Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 320);
                break;
            case Views.LocalPlaylistItem local:
                Title = local.Name;
                Subtitle = Loc.Plural("Tracks", local.Count);
                ImageUrl = Thumbnails.Sized(local.Cover, 320);
                break;
            case MoodItem mood:
                Title = mood.Title;
                IsMood = true;
                var color = mood.Color ?? 0xFF808080;
                StripeBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
                break;
        }
    }

    public MusicItem Item { get; }
    public string Title { get; } = "";
    public string Subtitle { get; } = "";
    public string? ImageUrl { get; }
    public bool IsRound { get; }
    public bool IsMood { get; }
    public Brush StripeBrush { get; } = new SolidColorBrush(Colors.Transparent);
}

/// <summary>Карточка 160 px для полок и сеток (§5.4). У исполнителя — круг.</summary>
public sealed partial class MediaCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(CardVm), typeof(MediaCard), new PropertyMetadata(null, (d, _) => ((MediaCard)d).OnCardChanged()));

    public MediaCard()
    {
        InitializeComponent();
    }

    public CardVm Card
    {
        get => (CardVm)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    private void OnCardChanged()
    {
        if (Card is null) return;
        AutomationProperties.SetName(this, string.Join(", ", new[] { Card.Title, Card.Subtitle }.Where(s => !string.IsNullOrEmpty(s))));
        Bindings.Update();
    }

    public static CornerRadius Corner(bool round) => round ? new CornerRadius(80) : new CornerRadius(8);

    public static TextAlignment Align(bool round) => round ? TextAlignment.Center : TextAlignment.Left;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
