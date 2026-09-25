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

    private async void OnStreamInfoClick(object sender, RoutedEventArgs e) => await StreamInfoDialog.ShowAsync(XamlRoot, ViewModel.Engine);

    /// <summary>«Очередь»: панель справа.</summary>
    private void OnQueueClick(object sender, RoutedEventArgs e) => App.Current?.Window?.ToggleQueue();

    /// <summary>Кнопка «Очередь» нажата, пока панель открыта.</summary>
    public void SetQueueOpen(bool open) => QueueButton.IsChecked = open;

    private void OnMiniClick(object sender, RoutedEventArgs e) => App.Current?.Window?.OpenMiniPlayer();

    /// <summary>
    /// Таймер сна (§4): 15, 30, 45 или 60 минут и «До конца трека»; заведённый — с остатком и «Выключить таймер».
    /// Пункты собираются при открытии меню синхронно: всё известно заранее, меню не дёргается.
    /// </summary>
    private void OnMoreOpening(object sender, object e)
    {
        var engine = ViewModel.Engine;
        SleepMenu.Items.Clear();
        SleepMenu.Text = engine.SleepAt is { } at
            ? Loc.Format("SleepTimerLeftFormat", Loc.Plural("MinutesLeft", Math.Max(1, (long)Math.Ceiling((at - DateTimeOffset.Now).TotalMinutes))))
            : engine.SleepAtTrackEnd ? Loc.Format("SleepTimerLeftFormat", Loc.Get("SleepUntilTrackEnd")) : Loc.Get("SleepTimer");
        foreach (var minutes in new[] { 15, 30, 45, 60 })
        {
            var item = new MenuFlyoutItem { Text = Loc.Plural("Minutes", minutes) };
            item.Click += (_, _) => engine.SetSleepTimer(TimeSpan.FromMinutes(minutes));
            SleepMenu.Items.Add(item);
        }
        var trackEnd = new MenuFlyoutItem { Text = Loc.Get("SleepUntilTrackEnd") };
        trackEnd.Click += (_, _) => engine.SetSleepAtTrackEnd();
        SleepMenu.Items.Add(trackEnd);
        if (engine.SleepTimerSet)
        {
            SleepMenu.Items.Add(new MenuFlyoutSeparator());
            var off = new MenuFlyoutItem { Text = Loc.Get("SleepTimerOff") };
            off.Click += (_, _) => engine.CancelSleepTimer();
            SleepMenu.Items.Add(off);
        }
    }
}
