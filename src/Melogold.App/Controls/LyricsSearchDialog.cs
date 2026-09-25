using Melogold.App.Services;
using Melogold.Core.Domain;
using Melogold.InnerTube.Lyrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// «Найти текст» (Android <c>LrcLibSearchDialog</c>): поиск по LRCLIB — поле сверху, под ним треки, у каждого
/// длительность и синхронный ли текст; «Импорт из файла» — TTML, LRC или простой текст.
/// </summary>
public static class LyricsSearchDialog
{
    public static async Task ShowAsync(XamlRoot root, LyricsService lyrics, string query)
    {
        var box = new TextBox { Text = query, PlaceholderText = Loc.Get("LyricsFind"), IsSpellCheckEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Loc.Get("LyricsFind"));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Height = 320 };
        var ring = new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var empty = new TextBlock
        {
            Text = Loc.Get("NoLyricsFound"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = Visibility.Collapsed,
        };
        var results = new Grid { Height = 320 };
        results.Children.Add(list);
        results.Children.Add(ring);
        results.Children.Add(empty);
        var content = new StackPanel { Spacing = 12, Width = 480 };
        content.Children.Add(box);
        content.Children.Add(results);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("ChooseLyricTrack"),
            Content = content,
            SecondaryButtonText = Loc.Get("LyricsImportFile"),
            CloseButtonText = Loc.Get("Cancel"),
        };

        CancellationTokenSource? search = null;
        async Task SearchAsync(string text, int delayMs)
        {
            search?.Cancel();
            var cancel = search = new CancellationTokenSource();
            ring.Visibility = Visibility.Visible;
            empty.Visibility = Visibility.Collapsed;
            try
            {
                // Ждём, пока человек допечатает
                await Task.Delay(delayMs, cancel.Token);
                var found = text.Trim().Length == 0 ? [] : await Task.Run(() => lyrics.LrcLib.SearchAsync(text.Trim(), cancel.Token), cancel.Token);
                if (cancel.IsCancellationRequested) return;
                list.ItemsSource = found.Select(Row).ToList();
                empty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException)
            {
                Log.Warn("LRCLIB search failed", e);
                list.ItemsSource = null;
                empty.Visibility = Visibility.Visible;
            }
            ring.Visibility = Visibility.Collapsed;
        }

        box.TextChanged += (_, _) => _ = SearchAsync(box.Text, 700);
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: LrcLibTrack track })
            {
                lyrics.UseLrcLib(track);
                dialog.Hide();
            }
        };
        _ = SearchAsync(query, 0);
        if (await dialog.ShowAsync() == ContentDialogResult.Secondary) await ImportAsync(lyrics);
        search?.Cancel();
    }

    private static FrameworkElement Row(LrcLibTrack track)
    {
        var kind = Loc.Get(string.IsNullOrWhiteSpace(track.SyncedLyrics) ? "LyricsResultPlain" : "LyricsResultSynced");
        var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6), Tag = track };
        panel.Children.Add(new TextBlock { Text = $"{track.ArtistName} — {track.TrackName}", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Durations.Format((long)(track.Duration * 1000))} · {kind}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, $"{track.ArtistName} — {track.TrackName}, {kind}");
        return panel;
    }

    /// <summary>Текст из файла: TTML, LRC или простой текст.</summary>
    public static async Task ImportAsync(LyricsService lyrics)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
        foreach (var type in new[] { ".lrc", ".ttml", ".xml", ".txt" }) picker.FileTypeFilter.Add(type);
        if (App.Current?.Window is not { } window) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var snackbar = App.Services.GetRequiredService<Snackbar>();
        string text;
        try
        {
            text = await File.ReadAllTextAsync(file.Path);
        }
        catch (IOException e)
        {
            Log.Warn("Lyrics file unreadable", e);
            snackbar.Show(Loc.Get("LyricsImportFailed"));
            return;
        }
        snackbar.Show(Loc.Get(lyrics.Import(text) ? "LyricsImported" : "LyricsImportFailed"));
    }
}
