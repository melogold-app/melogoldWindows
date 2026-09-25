using Melogold.Core.Data;
using Melogold.Core.Music;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Services;

/// <summary>
/// «Добавить в плейлист…» (REWRITE §3.11.4): флажки у своих плейлистов (отмечены те, где трек уже есть), «Новый
/// плейлист», добавление в конец. Для коллекции — только добавление.
/// </summary>
public static class PlaylistPicker
{
    public static void Show(Track track, Library library, Snackbar snackbar) => _ = ShowAsync([track], library, snackbar);

    public static async Task ShowAsync(IReadOnlyList<Track> tracks, Library library, Snackbar snackbar)
    {
        if (tracks.Count == 0 || App.Current?.Window?.Content.XamlRoot is not { } root) return;
        var single = tracks.Count == 1 ? tracks[0] : null;
        var playlists = library.Playlists();
        var containing = single is null ? [] : library.PlaylistsContaining(single.VideoId);

        var list = new StackPanel { Spacing = 2 };
        var boxes = new List<(LocalPlaylist Playlist, CheckBox Box)>();
        foreach (var playlist in playlists)
        {
            var box = new CheckBox { Content = playlist.Name, IsChecked = containing.Contains(playlist.Id) };
            boxes.Add((playlist, box));
            list.Children.Add(box);
        }
        var name = new TextBox { PlaceholderText = Loc.Get("NewPlaylistName"), Header = Loc.Get("NewPlaylist") };
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        if (playlists.Count > 0)
        {
            content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        }
        else content.Children.Add(new TextBlock { Text = Loc.Get("NoPlaylistsYet"), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        content.Children.Add(name);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("AddToPlaylistTitle"),
            Content = content,
            PrimaryButtonText = Loc.Get("Done"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var changed = new List<string>();
        foreach (var (playlist, box) in boxes)
        {
            var wanted = box.IsChecked == true;
            var had = containing.Contains(playlist.Id);
            if (wanted && !had)
            {
                if (library.AddToPlaylist(playlist.Id, tracks) > 0) changed.Add(playlist.Name);
            }
            else if (!wanted && had && single is not null)
            {
                library.RemoveFromPlaylist(playlist.Id, single.VideoId);
                changed.Add(playlist.Name);
            }
        }
        if (!string.IsNullOrWhiteSpace(name.Text))
        {
            library.CreatePlaylist(name.Text, tracks);
            changed.Add(name.Text.Trim());
        }
        if (changed.Count == 1) snackbar.Show(Loc.Format("AddedToPlaylistFormat", changed[0]));
        else if (changed.Count > 1) snackbar.Show(Loc.Plural("ChangedInPlaylists", changed.Count));
    }
}
