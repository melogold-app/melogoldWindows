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
    /// <summary>Причина ошибки коротко → объяснение целиком.</summary>
    private void OnErrorClick(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);

    private void OnDeviceClick(object sender, RoutedEventArgs e) => ShowDevices(DeviceButton);

    /// <summary>
    /// «Устройство» (tasks/0017): «Это устройство» и звук Windows; «Другие устройства» — значок, имя, «В сети» или «Не в
    /// сети», что там играет, громкость. Выбрать другое — плеер становится пультом.
    /// </summary>
    private async void ShowDevices(FrameworkElement anchor)
    {
        var remote = ViewModel.Remote.Remote;
        var panel = new StackPanel { Spacing = 2, Width = 340 };
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        panel.Children.Add(Heading(Loc.Get("RemoteDevice")));
        panel.Children.Add(DeviceRow(DeviceSymbols.Glyph("windows"), Loc.Get("RemoteThisDevice"), null, remote.Target is null, true, () =>
        {
            flyout.Hide();
            remote.Disconnect();
        }));
        var sound = new HyperlinkButton { Content = Loc.Get("RemoteSystemSound"), Margin = new Thickness(36, 0, 0, 0) };
        sound.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        panel.Children.Add(sound);
        var others = Heading(Loc.Get("RemoteOtherDevices"));
        others.Margin = new Thickness(0, 12, 0, 4);
        panel.Children.Add(others);
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 4, 0, 4) });
        panel.Children.Add(list);
        flyout.ShowAt(anchor);

        IReadOnlyList<Melogold.Server.RemoteDevice> devices;
        try
        {
            devices = await remote.DevicesAsync();
        }
        catch (Melogold.Server.ApiException e)
        {
            list.Children.Clear();
            list.Children.Add(Note(Loc.Get(e.IsNetwork ? "ErrorOffline" : "ErrorUnknown")));
            return;
        }
        list.Children.Clear();
        if (devices.Count == 0) list.Children.Add(Note(Loc.Get("RemoteNoOtherDevices")));
        foreach (var device in devices.OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCulture))
        {
            var details = new List<string>();
            if (!device.Online) details.Add(Loc.Get("RemoteOffline"));
            else if (!device.Controllable) details.Add(Loc.Get("RemoteNotControllable"));
            else if (device.Playing?.Track is { } playing) details.Add(string.IsNullOrEmpty(playing.ArtistsText) ? playing.Title : $"{playing.ArtistsText} — {playing.Title}");
            else details.Add(Loc.Get("RemoteOnline"));
            if (device.Online && device.Volume is { } volume) details.Add(Loc.Format("RemoteVolumeFormat", volume));
            var chosen = remote.Target?.DeviceId == device.DeviceId;
            list.Children.Add(DeviceRow(DeviceSymbols.Glyph(device.Platform), device.Name, string.Join(" · ", details), chosen, device.Online && device.Controllable, () =>
            {
                flyout.Hide();
                _ = remote.ConnectAsync(device);
            }));
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 4, 0, 4),
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static Button DeviceRow(string glyph, string name, string? detail, bool chosen, bool enabled, Action choose)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (chosen)
        {
            var check = new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        var button = new Button
        {
            Content = grid,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, string.IsNullOrEmpty(detail) ? name : $"{name}, {detail}");
        if (chosen) Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, Loc.Get("RemoteChosen"));
        button.Click += (_, _) => choose();
        return button;
    }

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
        AddHiddenControls(menu.Items);
        return menu;
    }

    /// <summary>
    /// Что спрятала узкая панель (VisualStateManager в PlayerBar.xaml) — в начале меню, с сочетаниями клавиш справа:
    /// ♡, перемешать, повтор, звук, мини-плеер.
    /// </summary>
    private void AddHiddenControls(IList<MenuFlyoutItemBase> items)
    {
        var hidden = new List<MenuFlyoutItemBase>();
        if (LikeButton.Visibility == Visibility.Collapsed)
            hidden.Add(Command(Loc.Get(ViewModel.IsLiked ? "MenuFavoriteRemove" : "MenuFavoriteAdd"), HeartGlyph(ViewModel.IsLiked), "Ctrl+D", ViewModel.ToggleLikeCommand));
        if (ShuffleButton.Visibility == Visibility.Collapsed)
        {
            var shuffle = new ToggleMenuFlyoutItem { Text = Loc.Get("ShortcutShuffle"), IsChecked = ViewModel.Shuffle, Icon = new FontIcon { Glyph = "\uE8B1" }, KeyboardAcceleratorTextOverride = "Ctrl+H" };
            shuffle.Click += (_, _) => ViewModel.ToggleShuffleCommand.Execute(null);
            hidden.Add(shuffle);
        }
        if (RepeatButton.Visibility == Visibility.Collapsed)
            hidden.Add(Command(ViewModel.RepeatLabel, ViewModel.RepeatGlyph, "Ctrl+T", ViewModel.CycleRepeatCommand));
        if (DeviceButton.Visibility == Visibility.Collapsed && ViewModel.RemoteAvailable)
        {
            var device = new MenuFlyoutItem { Text = Loc.Get("RemoteDevice"), Icon = new FontIcon { Glyph = ViewModel.DeviceGlyph } };
            device.Click += (_, _) => ShowDevices(MoreButton);
            hidden.Add(device);
        }
        if (MiniButton.Visibility == Visibility.Collapsed)
        {
            var mini = new MenuFlyoutItem { Text = Loc.Get("ShortcutMini"), Icon = new FontIcon { Glyph = "\uE944" }, KeyboardAcceleratorTextOverride = "Ctrl+Shift+M" };
            mini.Click += (_, _) => App.Current?.Window?.OpenMiniPlayer();
            hidden.Add(mini);
        }
        if (hidden.Count == 0) return;
        hidden.Add(new MenuFlyoutSeparator());
        for (var i = 0; i < hidden.Count; i++) items.Insert(i, hidden[i]);
    }

    private static MenuFlyoutItem Command(string text, string glyph, string keys, System.Windows.Input.ICommand command)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, KeyboardAcceleratorTextOverride = keys };
        item.Click += (_, _) => command.Execute(null);
        return item;
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
