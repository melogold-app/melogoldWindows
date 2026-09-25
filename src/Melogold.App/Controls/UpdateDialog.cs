using Melogold.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// «Вышла новая версия» (docs/PROMPT.md §3): что нового, размер, «Обновить» и «Позже». После «Обновить» окно не
/// закрывается — в нём прогресс скачивания; установщик закрывает Melogold и запускает его снова.
/// </summary>
public static class UpdateDialog
{
    /// <summary>Открыто ли уже окно обновления: второе не показывается.</summary>
    private static bool _open;

    public static async Task ShowAsync(XamlRoot root, UpdateService updates)
    {
        if (_open || updates.Available is not { } available) return;
        var size = UpdateService.Asset(available)?.SizeBytes ?? 0;
        var status = new TextBlock
        {
            Text = Loc.Format("UpdateAvailableText", FormatSize(size)),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var progress = new ProgressBar { Visibility = Visibility.Collapsed, Maximum = 100 };
        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(status);
        if (available.LocalizedNotes is { Length: > 0 } notes)
        {
            body.Children.Add(new TextBlock { Text = Loc.Get("UpdateWhatsNew"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
        }
        body.Children.Add(progress);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Format("UpdateAvailableTitle", available.Version),
            Content = body,
            PrimaryButtonText = Loc.Get("UpdateAction"),
            CloseButtonText = Loc.Get("UpdateLater"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Show()
        {
            progress.Value = updates.Progress;
            status.Text = updates.State switch
            {
                UpdateState.Downloading => Loc.Format("UpdateDownloadingFormat", updates.Progress),
                UpdateState.Installing => Loc.Get("UpdateInstalling"),
                UpdateState.Failed => Loc.Get("UpdateFailed"),
                _ => status.Text,
            };
            var busy = updates.State is UpdateState.Downloading or UpdateState.Installing;
            dialog.IsPrimaryButtonEnabled = !busy;
            progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => dialog.DispatcherQueue.TryEnqueue(Show);
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            // Окно остаётся открытым: в нём прогресс, а после скачивания Melogold закроется сам
            var deferral = e.GetDeferral();
            e.Cancel = true;
            deferral.Complete();
            await updates.InstallAsync();
        };
        updates.PropertyChanged += OnChanged;
        _open = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Уже открыт другой диалог: значок «!» у «Настроек» останется
            Log.Warn("Update dialog not shown", ex);
        }
        finally
        {
            _open = false;
            updates.PropertyChanged -= OnChanged;
        }
    }

    private static string FormatSize(long bytes) => Loc.Format("SizeMegabytesFormat", (bytes / 1024.0 / 1024).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
}
