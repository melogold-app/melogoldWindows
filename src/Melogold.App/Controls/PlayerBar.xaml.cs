using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Melogold.App.Controls;

/// <summary>Панель воспроизведения во всю ширину окна (§5.2).</summary>
public sealed partial class PlayerBar : UserControl
{
    public PlayerBar()
    {
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
        // Перемотка: пока ползунок держат, позиция не прыгает от таймера; отпустили — переход
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ViewModel.BeginSeek()), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ViewModel.EndSeek(SeekSlider.Value)), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => ViewModel.EndSeek(SeekSlider.Value)), true);
        SeekSlider.ValueChanged += (_, e) =>
        {
            // Клавиатура (стрелки, PageUp/PageDown) меняет значение без указателя
            if (SeekSlider.FocusState == FocusState.Keyboard && Math.Abs(e.NewValue - ViewModel.Position) > 1.5) ViewModel.EndSeek(e.NewValue);
            else ViewModel.PreviewSeek(e.NewValue);
        };
    }

    public PlayerViewModel ViewModel { get; }

    public static Visibility IsSet(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static string HeartGlyph(bool liked) => liked ? "" : "";

    public static bool? IsRepeat(RepeatMode mode) => mode != RepeatMode.Off;

    /// <summary>Обложка открывает «Сейчас играет» (§5.2).</summary>
    private void OnArtworkClick(object sender, RoutedEventArgs e) => App.Current?.Window?.NowPlaying.Toggle();

    /// <summary>«Текст»: «Сейчас играет» сразу на тексте.</summary>
    private void OnLyricsClick(object sender, RoutedEventArgs e) => App.Current?.Window?.NowPlaying.Toggle(lyrics: true);

    /// <summary>Кнопка «Текст» нажата, пока «Сейчас играет» открыто.</summary>
    public void SetNowPlayingOpen(bool open) => LyricsButton.IsChecked = open;

    private void OnTitleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Track is not { } track) return;
        App.Services.GetRequiredService<TrackActions>().OpenAlbumOrArtist(track);
    }

    private void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Track is not { } track) return;
        App.Services.GetRequiredService<TrackActions>().OpenArtist(track, SubtitleLink);
    }

    private async void OnStreamInfoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Engine.Stream is not { } stream) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
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
