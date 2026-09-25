using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    /// <summary>Подсказка с сочетанием клавиш: «Пауза (Пробел)».</summary>
    public static string Hint(string label, string keys) => $"{label} ({(keys == "Space" ? Loc.Get("KeySpace") : keys)})";

    /// <summary>Обложка открывает «Сейчас играет» (§5.2).</summary>
    private void OnArtworkClick(object sender, RoutedEventArgs e) => App.Current?.Window?.NowPlaying.Toggle(from: ArtworkButton);

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

    /// <summary>«Очередь»: панель справа.</summary>
    private void OnQueueClick(object sender, RoutedEventArgs e) => App.Current?.Window?.ToggleQueue();

    /// <summary>Кнопка «Очередь» нажата, пока панель открыта.</summary>
    public void SetQueueOpen(bool open) => QueueButton.IsChecked = open;

    private void OnMiniClick(object sender, RoutedEventArgs e) => App.Current?.Window?.OpenMiniPlayer();

    /// <summary>
    /// «…» — одно меню на всё, как на Android (REWRITE §3.10.5), а не своё у текста: играющий трек теми же пунктами,
    /// что в списках (кроме «Играть следующим», «В конец очереди» и ♡ — он рядом), группа «Текст», пока текст на
    /// экране, таймер сна и сведения о потоке, в конце — «Не показывать этот трек». Собирается при нажатии синхронно:
    /// всё известно заранее, меню не дёргается.
    /// </summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (BuildMenu() is { } menu) menu.ShowAt(MoreButton);
    }

    /// <summary>Правый клик, клавиша меню или Shift+F10 по треку слева — то же меню, у указателя.</summary>
    private void OnTrackContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (BuildMenu() is not { } menu) return;
        if (args.TryGetPosition(sender, out var point)) menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else menu.ShowAt((FrameworkElement)sender);
        args.Handled = true;
    }

    private MenuFlyout? BuildMenu()
    {
        if (ViewModel.Track is not { } track) return null;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        App.Services.GetRequiredService<TrackActions>().AddItems(menu.Items, track, new TrackContext.Player(), beforeRemovals: items =>
        {
            App.Current?.Window?.NowPlaying.AddLyricsItems(items);
            items.Add(new MenuFlyoutSeparator());
            items.Add(SleepMenu());
            var info = new MenuFlyoutItem { Text = Loc.Get("MenuStreamInfo"), Icon = new FontIcon { Glyph = "" } };
            info.Click += async (_, _) => await StreamInfoDialog.ShowAsync(XamlRoot, ViewModel.Engine);
            items.Add(info);
            var keys = new MenuFlyoutItem { Text = Loc.Get("MenuShortcuts"), Icon = new FontIcon { Glyph = "" } };
            keys.KeyboardAcceleratorTextOverride = "F1";
            keys.Click += (_, _) => App.Current?.Window?.ShowShortcuts();
            items.Add(keys);
        }, anchor: MoreButton);
        return menu;
    }

    /// <summary>Длинное название обрезано многоточием — целиком в подсказке.</summary>
    private void OnTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) =>
        ToolTipService.SetToolTip(sender, sender.IsTextTrimmed ? sender.Text : null);

    private void OnVolumeWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ViewModel.ChangeVolume(delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    /// <summary>Таймер сна (§4): 15, 30, 45 или 60 минут и «До конца трека»; заведённый — с остатком и «Выключить таймер».</summary>
    private MenuFlyoutSubItem SleepMenu()
    {
        var engine = ViewModel.Engine;
        var menu = new MenuFlyoutSubItem
        {
            Icon = new FontIcon { Glyph = "" },
            Text = engine.SleepAt is { } at
                ? Loc.Format("SleepTimerLeftFormat", Loc.Plural("MinutesLeft", Math.Max(1, (long)Math.Ceiling((at - DateTimeOffset.Now).TotalMinutes))))
                : engine.SleepAtTrackEnd ? Loc.Format("SleepTimerLeftFormat", Loc.Get("SleepUntilTrackEnd")) : Loc.Get("SleepTimer"),
        };
        foreach (var minutes in new[] { 15, 30, 45, 60 })
        {
            var item = new MenuFlyoutItem { Text = Loc.Plural("Minutes", minutes) };
            item.Click += (_, _) => engine.SetSleepTimer(TimeSpan.FromMinutes(minutes));
            menu.Items.Add(item);
        }
        var trackEnd = new MenuFlyoutItem { Text = Loc.Get("SleepUntilTrackEnd") };
        trackEnd.Click += (_, _) => engine.SetSleepAtTrackEnd();
        menu.Items.Add(trackEnd);
        if (engine.SleepTimerSet)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var off = new MenuFlyoutItem { Text = Loc.Get("SleepTimerOff") };
            off.Click += (_, _) => engine.CancelSleepTimer();
            menu.Items.Add(off);
        }
        return menu;
    }
}
