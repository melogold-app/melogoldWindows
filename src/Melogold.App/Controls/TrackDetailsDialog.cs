using Melogold.App.Services;
using Melogold.Core.Music;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// «Изменить сведения…» (tasks/0011): своё название, исполнитель и альбом трека — так альбом, собранный из разрозненных
/// видео YouTube, выглядит одним альбомом на всех устройствах. В пустом поле серым — как на YouTube. «Как на YouTube»
/// снимает правку целиком.
/// </summary>
public static class TrackDetailsDialog
{
    /// <summary>Новая правка; <see cref="TrackOverride.None"/> — «Как на YouTube»; null — отмена.</summary>
    public static async Task<TrackOverride?> ShowAsync(XamlRoot root, Track track, TrackOverride? current)
    {
        TextBox Field(string key, string? youTube, string? value) => new()
        {
            Header = Loc.Get(key),
            Text = value ?? "",
            PlaceholderText = youTube ?? "",
            MaxLength = TrackOverride.FieldMax,
        };
        var title = Field("TrackDetailsName", track.Title, current?.Title);
        var artist = Field("TrackDetailsArtist", track.ArtistsText, current?.ArtistsText);
        var album = Field("TrackDetailsAlbum", track.AlbumTitle, current?.AlbumTitle);
        var fields = new StackPanel { Spacing = 12, MinWidth = 360 };
        fields.Children.Add(title);
        fields.Children.Add(artist);
        fields.Children.Add(album);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("TrackDetailsTitle"),
            Content = fields,
            PrimaryButtonText = Loc.Get("TrackDetailsSave"),
            SecondaryButtonText = Loc.Get("TrackDetailsReset"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = current is { IsEmpty: false },
        };
        title.Loaded += (_, _) => title.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => TrackOverride.Of(title.Text, artist.Text, album.Text),
            ContentDialogResult.Secondary => TrackOverride.None,
            _ => null,
        };
    }

    /// <summary>«Указать альбом…» у выделенного: одно поле; null — отмена.</summary>
    public static async Task<string?> AskAlbumAsync(XamlRoot root, string suggested)
    {
        var box = new TextBox { Header = Loc.Get("TrackDetailsAlbum"), Text = suggested, MaxLength = TrackOverride.FieldMax, MinWidth = 360 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("SelectionSetAlbumTitle"),
            Content = box,
            PrimaryButtonText = Loc.Get("TrackDetailsSave"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = suggested.Trim().Length > 0,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
