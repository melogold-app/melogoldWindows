using Melogold.App.Services;
using Melogold.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>«Сведения о потоке»: из меню плеера и из «Настройки › Воспроизведение».</summary>
public static class StreamInfoDialog
{
    public static async Task ShowAsync(XamlRoot root, PlayerEngine engine)
    {
        if (engine.Stream is not { } stream) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("StreamInfoTitle"),
            CloseButtonText = Loc.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = string.Join("\n",
                    $"videoId: {stream.VideoId}",
                    $"itag: {stream.Itag} · {stream.Codec}",
                    $"{Loc.Get("StreamBitrate")}: {(stream.Bitrate ?? 0) / 1000} kbps",
                    $"{Loc.Get("StreamSource")}: {stream.Source}",
                    stream.LoudnessDb is { } db ? $"loudnessDb: {db:0.0}" : "loudnessDb: —"),
            },
        };
        await dialog.ShowAsync();
    }
}
