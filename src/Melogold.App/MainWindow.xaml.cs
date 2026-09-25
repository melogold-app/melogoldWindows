using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Melogold.App;

/// <summary>Подсказка поля поиска: недавний запрос, подсказка YouTube Music, трек из библиотеки или ссылка.</summary>
public sealed record SuggestionVm(string Glyph, string Text, string? Detail = null, Track? Track = null, bool IsLink = false)
{
    public Visibility HasDetail => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class MainWindow : Window
{
    private readonly Navigator _navigator;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, Frame> _frames = [];
    private CancellationTokenSource? _suggestions;

    private static readonly Section[] Sections =
    [
        new("trends", typeof(TrendsPage)),
        new("new", typeof(NewPage)),
        new("library", typeof(LibraryPage)),
        new("settings", typeof(SettingsPage)),
    ];

    public MainWindow()
    {
        _navigator = App.Services.GetRequiredService<Navigator>();
        _settings = App.Services.GetRequiredService<SettingsStore>();
        Snackbar = App.Services.GetRequiredService<Snackbar>();
        InitializeComponent();
        SearchBox.Loaded += (_, _) => AttachSearchLayout();
        AnimateSnackbar();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "melogold.ico"));
        ApplyBackdrop();
        ApplyTheme();
        // Меньше 500×500 окно не сжимается: у́же панель плеера и заголовок разваливаются (пользователь показал окно в 290)
        Root.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            Content.XamlRoot.Changed += (_, _) => ApplyMinimumSize();
        };
#if DEBUG
        DebugSnapshot.Start(Root);
#endif
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsStore.Theme)) ApplyTheme();
        };

        // Пункт настроек создаётся шаблоном NavigationView: подпись «Настройки» (глоссарий), а не системное «Параметры»
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = Loc.Get("NavSettings");
                // Экранный диктор читает то же, что видно, а не имя из шаблона
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, Loc.Get("NavSettings"));
                ToolTipService.SetToolTip(settingsItem, Loc.Get("NavSettings"));
                settingsItem.Tag = "settings";
                // Клавиша доступа (Alt, затем буква), как у разделов из x:Uid
                settingsItem.AccessKey = Loc.Get("AccessKeySettings");
                ShowUpdateBadge();
            }
            if (_navigator.Current == "settings") Nav.SelectedItem = Nav.SettingsItem;
        };

        foreach (var section in Sections)
        {
            var frame = new Frame { Visibility = Visibility.Collapsed };
            _frames[section.Key] = frame;
            SectionHost.Children.Add(frame);
            _navigator.Register(section, frame);
        }
        _navigator.SectionShown += OnSectionShown;
        _navigator.Changed += UpdateChrome;
        // «Сейчас играет» — поверх разделов; переход куда-либо его закрывает
        Grid.SetRow(NowPlaying, 1);
        Root.Children.Add(NowPlaying);
        _navigator.Changed += () => NowPlaying.Close();
        NowPlaying.OpenChanged += open =>
        {
            if (!open && _fullScreen) SetFullScreen(false);
            UpdateChrome();
        };
        // Очередь — панель справа: в широком окне сдвигает содержимое, в узком ложится поверх
        Grid.SetRow(Queue, 1);
        Queue.HorizontalAlignment = HorizontalAlignment.Right;
        Root.Children.Add(Queue);
        Queue.OpenChanged += open =>
        {
            Player.SetQueueOpen(open);
            LayoutQueue();
        };
        Root.SizeChanged += (_, _) => LayoutQueue();
        _navigator.Show(_settings.LastSection);

        var player = App.Services.GetRequiredService<PlayerViewModel>();
        // Сочетания клавиш — все в окне «Сочетания клавиш» (F1, Ctrl+/; ShortcutsDialog). Стрелки с Ctrl и Shift в поле
        // ввода остаются полю: там они двигают курсор и выделяют текст
        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control, shift = VirtualKeyModifiers.Shift;
        // Сочетания висят на корне окна: подсказку «Ctrl+F» WinUI показывал бы над всем окном
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        void Key(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool inText = true) =>
            Root.KeyboardAccelerators.Add(Accelerator(key, modifiers, action, inText ? null : FocusInTextInput));
        Key(VirtualKey.F, ctrl, FocusSearch);
        Key(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigator.GoBack(), inText: false);
        Key(VirtualKey.Escape, VirtualKeyModifiers.None, GoBack);
        Key(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullScreen);
        Key(VirtualKey.F1, VirtualKeyModifiers.None, ShowShortcuts);
        Key((VirtualKey)191, ctrl, ShowShortcuts); // Ctrl+/
        // Воспроизведение
        Key(VirtualKey.Right, ctrl, () => player.Engine.Next(), inText: false);
        Key(VirtualKey.Left, ctrl, () => player.Engine.Previous(), inText: false);
        Key(VirtualKey.Right, shift, () => SeekBy(5), inText: false);
        Key(VirtualKey.Left, shift, () => SeekBy(-5), inText: false);
        Key(VirtualKey.Up, ctrl, () => player.ChangeVolume(5), inText: false);
        Key(VirtualKey.Down, ctrl, () => player.ChangeVolume(-5), inText: false);
        // Без звука — Ctrl+M и просто M (OnRootKeyDown), как привыкли: в нужный момент заглушить одной клавишей
        Key(VirtualKey.M, ctrl, () => player.ToggleMuteCommand.Execute(null));
        Key(VirtualKey.H, ctrl, () => player.ToggleShuffleCommand.Execute(null));
        Key(VirtualKey.T, ctrl, () => player.CycleRepeatCommand.Execute(null));
        Key(VirtualKey.D, ctrl, () => player.ToggleLikeCommand.Execute(null));
        // Окно и разделы
        Key(VirtualKey.Number1, ctrl, () => ShowSection("trends"));
        Key(VirtualKey.Number2, ctrl, () => ShowSection("new"));
        Key(VirtualKey.Number3, ctrl, () => ShowSection("library"));
        Key((VirtualKey)188, ctrl, () => ShowSection("settings")); // Ctrl+,
        Key(VirtualKey.L, ctrl, () => NowPlaying.Toggle(lyrics: true));
        Key(VirtualKey.Q, ctrl, ToggleQueue);
        Key(VirtualKey.M, ctrl | shift, OpenMiniPlayer);
        // Перетащить в окно ссылку YouTube или melogold:// — открыть; файл копии библиотеки — «Импорт копии»
        Root.AllowDrop = true;
        Root.DragOver += OnRootDragOver;
        Root.Drop += OnRootDrop;
        Root.KeyDown += OnRootKeyDown;
        Root.PreviewKeyDown += OnRootPreviewKeyDown;
        Root.PreviewKeyUp += OnRootPreviewKeyUp;
        Root.PointerPressed += OnRootPointerPressed;

        var updates = App.Services.GetRequiredService<UpdateService>();
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateService.HasUpdate)) DispatcherQueue.TryEnqueue(ShowUpdateBadge);
        };
        updates.Found += manifest => DispatcherQueue.TryEnqueue(() => AnnounceUpdate(manifest));
        Activated += (_, e) => _active = e.WindowActivationState != WindowActivationState.Deactivated;
        // Свой текст не поместился на сервер (413): он останется только здесь
        App.Services.GetRequiredService<Melogold.Server.LibrarySync>().LyricsRejected += _ => DispatcherQueue.TryEnqueue(() => Snackbar.Show(Loc.Get("LyricsTooLarge")));
        // Таймер сна сработал: воспроизведение на паузе — сказать об этом
        player.Engine.SleepTimerFired += () => DispatcherQueue.TryEnqueue(() => Snackbar.Show(Loc.Get("SleepTimerEnded")));
        // Кнопки ⏮ ⏯ ⏭ на миниатюре в панели задач — когда у окна уже есть кнопка на панели
        Activated += (_, _) =>
        {
            if (_taskbar is not null) return;
            _taskbar = new TaskbarButtons(player.Engine, WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue);
            _taskbar.Add();
        };

        // Размер в эффективных пикселях: при масштабе 150 % окно не должно выйти маленьким
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(820 * scale)));
        Closed += (_, _) =>
        {
            _settings.LastSection = _navigator.Current;
            player.SaveQueue();
            Snackbar.Dismiss(commit: true);
        };
    }

    public Snackbar Snackbar { get; }

    /// <summary>Очередь — панель справа около 320 px (§5.2).</summary>
    public QueuePanel Queue { get; } = new();

    private TaskbarButtons? _taskbar;
    private MiniPlayerWindow? _mini;

    public void ToggleQueue() => Queue.Toggle();

    private void LayoutQueue()
    {
        var wide = Root.ActualWidth >= 900;
        var margin = new Thickness(0, 0, Queue.IsOpen && wide ? Queue.Width : 0, 0);
        Nav.Margin = margin;
        NowPlaying.Margin = margin;
    }

    /// <summary>Мини-плеер: отдельное окно поверх остальных; главное окно на это время скрыто.</summary>
    public void OpenMiniPlayer()
    {
        if (_mini is not null)
        {
            _mini.Activate();
            return;
        }
        _mini = new MiniPlayerWindow();
        _mini.Closed += (_, _) =>
        {
            _mini = null;
            AppWindow.Show();
            Activate();
        };
        _mini.Activate();
        AppWindow.Hide();
    }

    public static Visibility IsSet(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Mica — на Windows 11; на Windows 10 — акрил, а где нет и его — обычный фон страницы (§3).</summary>
    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        else if (DesktopAcrylicController.IsSupported()) SystemBackdrop = new DesktopAcrylicBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private const double MinWindowWidth = 500, MinWindowHeight = 500;

    /// <summary>Наименьший размер окна в эффективных пикселях — в пикселях экрана с его масштабом (другой монитор — пересчёт).</summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter || Content?.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinWindowWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinWindowHeight * scale);
    }

    /// <summary>Плашка всплывает снизу и тает, как уведомление, а не появляется рывком.</summary>
    private void AnimateSnackbar()
    {
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SnackbarHost).Compositor;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(SnackbarHost, true);
        var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.InsertKeyFrame(0, 0);
        fadeIn.InsertKeyFrame(1, 1, easing);
        fadeIn.Duration = TimeSpan.FromMilliseconds(200);
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Target = "Translation";
        rise.InsertKeyFrame(0, new System.Numerics.Vector3(0, 16, 0));
        rise.InsertKeyFrame(1, System.Numerics.Vector3.Zero, easing);
        rise.Duration = fadeIn.Duration;
        var show = compositor.CreateAnimationGroup();
        show.Add(fadeIn);
        show.Add(rise);

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Target = "Opacity";
        fadeOut.InsertKeyFrame(0, 1);
        fadeOut.InsertKeyFrame(1, 0);
        fadeOut.Duration = TimeSpan.FromMilliseconds(150);

        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(SnackbarHost, show);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitHideAnimation(SnackbarHost, fadeOut);
    }

    /// <summary>Сочетание клавиш; <paramref name="skip"/> — когда оставить клавиши элементу в фокусе (поле ввода).</summary>
    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action, Func<bool>? skip = null)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            if (skip?.Invoke() == true) return;
            action();
            e.Handled = true;
        };
        return accelerator;
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        // Перетаскивание внутри окна (очередь, свой плейлист) — не наше
        if (e.Handled || e.DataView.Contains("Melogold.Internal")) return;
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("DropImport");
        }
        else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink)
                 || e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
            e.DragUIOverride.Caption = Loc.Get("DropOpen");
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var data = e.DataView;
        var deferral = e.GetDeferral();
        string? link = null, file = null;
        try
        {
            if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                file = (await data.GetStorageItemsAsync()).OfType<Windows.Storage.StorageFile>().FirstOrDefault()?.Path;
            else if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
                link = (await data.GetWebLinkAsync()).OriginalString;
            else if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                link = await data.GetTextAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn("Drop failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
        if (file is not null) await ImportFlow.ImportAsync(Content.XamlRoot, file);
        else if (!string.IsNullOrWhiteSpace(link)) App.Services.GetRequiredService<LinkRouter>().OpenText(link);
    }

    /// <summary>F1 и Ctrl+/: окно со всеми сочетаниями клавиш.</summary>
    public void ShowShortcuts() => _ = ShortcutsDialog.ShowAsync(Content.XamlRoot);

    /// <summary>Ctrl+1, 2, 3 и Ctrl+, — разделы, как нажатие в левой панели.</summary>
    private void ShowSection(string key)
    {
        NowPlaying.Close();
        if (key == _navigator.Current) _navigator.Reselect();
        else _navigator.Show(key);
    }

    /// <summary>Shift+→ и Shift+←: вперёд и назад на <paramref name="seconds"/> с.</summary>
    private static void SeekBy(double seconds)
    {
        var engine = App.Services.GetRequiredService<Melogold.Playback.PlayerEngine>();
        if (engine.Current is null) return;
        var target = engine.Position + TimeSpan.FromSeconds(seconds);
        var end = engine.Duration;
        engine.Seek(target < TimeSpan.Zero ? TimeSpan.Zero : end > TimeSpan.Zero && target > end ? end - TimeSpan.FromSeconds(1) : target);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    // ---------- Разделы ----------

    private void OnSectionShown(string key)
    {
        foreach (var (name, frame) in _frames) frame.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;
        Nav.SelectedItem = key == "settings"
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key);
        _settings.LastSection = key;
    }

    private void UpdateChrome()
    {
        AppTitleBar.IsBackButtonEnabled = _navigator.CanGoBack || NowPlaying.IsOpen;
        Player.SetNowPlayingOpen(NowPlaying.IsOpen);
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        NowPlaying.Close();
        var key = args.IsSettingsInvoked ? "settings" : (args.InvokedItemContainer?.Tag as string ?? Navigator.Start);
        if (key == _navigator.Current) _navigator.Reselect();
        else _navigator.Show(key);
        if (Nav.DisplayMode == NavigationViewDisplayMode.Minimal) Nav.IsPaneOpen = false;
    }

    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        // Меню-гамбургер в заголовке — только в узком окне, где панель спрятана
        AppTitleBar.IsPaneToggleButtonVisible = args.DisplayMode == NavigationViewDisplayMode.Minimal;
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnTitleBarBackRequested(TitleBar sender, object args) => GoBack();

    /// <summary>«Назад» и Esc сначала выходят из полноэкранного режима, потом закрывают «Сейчас играет».</summary>
    private void GoBack()
    {
        if (_fullScreen) SetFullScreen(false);
        else if (NowPlaying.EditorOpen) _ = NowPlaying.CloseEditorAsync();
        else if (NowPlaying.IsOpen) NowPlaying.Close();
        else _navigator.GoBack();
    }

    private bool _fullScreen;

    /// <summary>F11 — «Сейчас играет» на весь экран (§5.5): без строки заголовка, панель плеера остаётся.</summary>
    private void ToggleFullScreen()
    {
        if (_fullScreen)
        {
            SetFullScreen(false);
            return;
        }
        NowPlaying.Open();
        if (NowPlaying.IsOpen) SetFullScreen(true);
    }

    private void SetFullScreen(bool on)
    {
        _fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(NowPlaying, on ? 0 : 1);
        Grid.SetRowSpan(NowPlaying, on ? 2 : 1);
    }

    // ---------- Клавиатура и мышь ----------

    private bool FocusInTextInput() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox or RichEditBox;

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusInTextInput()) return;
        // «/» — поиск (§5.5)
        if (e.Key == (VirtualKey)191)
        {
            FocusSearch();
            e.Handled = true;
        }
        // M — без звука, как на YouTube; с Ctrl, Alt или Win — не наше
        else if (e.Key == VirtualKey.M && !ModifierDown(VirtualKey.Control) && !ModifierDown(VirtualKey.Menu) && !ModifierDown(VirtualKey.LeftWindows))
        {
            App.Services.GetRequiredService<PlayerViewModel>().ToggleMuteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool ModifierDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Пробел перехвачен до кнопки: её отпускание пробела тоже не должно нажать.</summary>
    private bool _spaceTaken;

    /// <summary>
    /// Пробел — play/pause (§5.5) раньше, чем его получит элемент в фокусе: после нажатия мышью фокус остаётся на
    /// кнопке, и пробел нажимал её снова (например, сворачивал текст). Не трогается ввод текста и элемент, на который
    /// перешли с клавиатуры (Tab): там пробел нажимает его, как везде в Windows.
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || FocusInTextInput()) return;
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is Control { FocusState: FocusState.Keyboard }) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        // Удержание пробела не переключает много раз
        if (!e.KeyStatus.WasKeyDown) App.Services.GetRequiredService<PlayerViewModel>().Engine.TogglePlayPause();
        _spaceTaken = true;
        e.Handled = true;
    }

    private void OnRootPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !_spaceTaken) return;
        _spaceTaken = false;
        e.Handled = true;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Боковая кнопка мыши «назад»
        if (e.GetCurrentPoint(Root).Properties.IsXButton1Pressed)
        {
            _navigator.GoBack();
            e.Handled = true;
        }
    }

    private void FocusSearch() => SearchBox.Focus(FocusState.Keyboard);

    private FrameworkElement? _searchColumn;

    /// <summary>Колонка содержимого строки заголовка: её ширина меняется с окном и с кнопками слева.</summary>
    private void AttachSearchLayout()
    {
        if (_searchColumn is not null) return;
        if (VisualTreeHelper.GetParent(SearchBox) is not FrameworkElement presenter || VisualTreeHelper.GetParent(presenter) is not FrameworkElement column) return;
        _searchColumn = column;
        column.SizeChanged += (_, _) => LayoutSearch();
        LayoutSearch();
    }

    /// <summary>
    /// Поиск по центру окна (§5.1), шириной около трети окна (240–520). TitleBar ставит его по центру своей колонки —
    /// между заголовком и кнопками окна, а они разной ширины; сдвиг до центра окна — отступом с одной стороны.
    /// </summary>
    private void LayoutSearch()
    {
        // В узком окне поиску не хватает места рядом с подписью «Melogold»: подпись прячется, значок остаётся
        var title = AppTitleBar.ActualWidth is > 0 and < 720 ? "" : "Melogold";
        if (AppTitleBar.Title != title) AppTitleBar.Title = title;
        if (_searchColumn is not { ActualWidth: > 0 } column || VisualTreeHelper.GetParent(SearchBox) is not FrameworkElement presenter) return;
        var bar = AppTitleBar.ActualWidth;
        var columnLeft = column.TransformToVisual(AppTitleBar).TransformPoint(default).X;
        // Место — от начала колонки до кнопок окна, а не ширина самой колонки: она подстраивается под поиск, и в
        // узком окне оба сжимались до 9 px. Немного места остаётся, чтобы окно можно было тащить за заголовок
        var captions = FixCaptionInset();
        // 48 — колонка TitleBar, за которую окно всегда можно утащить (TitleBarMinDragRegionWidth)
        var room = Math.Max(0, bar - columnLeft - captions - 48);
        var width = Math.Min(Math.Clamp(bar * 0.32, 180, 520), room);
        SearchBox.Width = width;
        // В узком окне TitleBar прижимает поиск влево — так и оставить
        if (presenter.HorizontalAlignment != HorizontalAlignment.Center || column.ActualWidth <= width + 1)
        {
            SearchBox.Margin = default;
            return;
        }
        var left = Math.Clamp((bar - width) / 2 - columnLeft, 0, column.ActualWidth - width);
        var shift = left - (column.ActualWidth - width) / 2;
        SearchBox.Margin = shift >= 0 ? new Thickness(2 * shift, 0, 0, 0) : new Thickness(0, 0, -2 * shift, 0);
    }

    /// <summary>
    /// Место под кнопки окна справа, в эффективных пикселях. TitleBar (WinAppSDK 2.3) ставит в свою колонку
    /// <c>RightPaddingColumn</c> <c>AppWindow.TitleBar.RightInset</c> без деления на масштаб экрана: при 225 % это
    /// 288 вместо 128, и в узком окне поиску не оставалось места (замер 2026-09-26). Здесь — верная ширина.
    /// </summary>
    private double FixCaptionInset()
    {
        var captions = AppWindow.TitleBar.RightInset / (Content.XamlRoot?.RasterizationScale ?? 1);
        if (VisualTreeHelper.GetChildrenCount(AppTitleBar) > 0 && VisualTreeHelper.GetChild(AppTitleBar, 0) is Grid layoutRoot
            && layoutRoot.FindName("RightPaddingColumn") is ColumnDefinition padding
            && Math.Abs(padding.Width.Value - captions) > 1)
            padding.Width = new GridLength(captions);
        return captions;
    }

    /// <summary>Есть обновление — значок «!» у пункта «Настройки» (§3), пока оно не установлено.</summary>
    private void ShowUpdateBadge()
    {
        if (Nav.SettingsItem is not NavigationViewItem item) return;
        item.InfoBadge = App.Services.GetRequiredService<UpdateService>().HasUpdate
            ? new InfoBadge { IconSource = new FontIconSource { Glyph = "", FontSize = 10 } }
            : null;
    }

    private bool _active = true;

    /// <summary>
    /// Нашлась новая версия: окно «Вышла новая версия», если Melogold перед глазами, иначе уведомление Windows — нажатие
    /// по нему открывает то же окно. О каждой версии — один раз; дальше остаётся значок «!» у «Настроек».
    /// </summary>
    private void AnnounceUpdate(UpdateManifest manifest)
    {
        var settings = App.Services.GetRequiredService<SettingsStore>();
        if (settings.UpdateAnnouncedVersion == manifest.Version) return;
        settings.UpdateAnnouncedVersion = manifest.Version;
        var updates = App.Services.GetRequiredService<UpdateService>();
        if (_active && AppWindow.IsVisible)
        {
            _ = UpdateDialog.ShowAsync(Content.XamlRoot, updates);
            return;
        }
        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text></text><text></text></binding></visual></toast>");
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(Loc.Format("UpdateAvailableTitle", manifest.Version)));
            texts[1].AppendChild(xml.CreateTextNode(Loc.Get("UpdateToastText")));
            var toast = new Windows.UI.Notifications.ToastNotification(xml);
            toast.Activated += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                AppWindow.Show();
                Activate();
                _ = UpdateDialog.ShowAsync(Content.XamlRoot, updates);
            });
            // Тот же AppUserModelID, что у ярлыка установщика: без ярлыка Windows уведомление не сохраняет
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("Melogold.Melogold").Show(toast);
        }
        catch (Exception e)
        {
            Log.Warn("Update notification not shown", e);
        }
    }

    /// <summary>«Сейчас играет»: страница поверх окна (§5.2).</summary>
    public NowPlayingView NowPlaying { get; } = new();

    /// <summary>«Найти музыку» из пустых экранов.</summary>
    public void FocusSearchBox() => FocusSearch();

    private void OnSnackbarAction(object sender, RoutedEventArgs e) => Snackbar.InvokeAction();

    // ---------- Поиск (§5.4): до ввода — недавние запросы; при вводе — ссылка, «В библиотеке», подсказки ----------

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) ShowRecentSearches();
    }

    private void ShowRecentSearches()
    {
        // История поиска на паузе — недавние не показываются
        List<string> recent = _settings.PauseSearchHistory ? [] : App.Services.GetRequiredService<Library>().RecentSearches(8);
        SearchBox.ItemsSource = recent.Select(q => new SuggestionVm("", q)).ToList();
        SearchBox.IsSuggestionListOpen = recent.Count > 0;
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _suggestions?.Cancel();
        var text = sender.Text.Trim();
        if (text.Length == 0)
        {
            ShowRecentSearches();
            return;
        }
        var cts = _suggestions = new CancellationTokenSource();
        var items = new List<SuggestionVm>();
        var target = YouTubeLinkParser.Parse(text);
        if (target is not LinkTarget.Search and not LinkTarget.Unsupported)
        {
            var kind = target switch
            {
                LinkTarget.Video => "LinkKindVideo",
                LinkTarget.Playlist => "LinkKindPlaylist",
                LinkTarget.Album => "LinkKindAlbum",
                LinkTarget.External => null,
                _ => "LinkKindChannel",
            };
            items.Add(new SuggestionVm("", kind is null ? Loc.Get("LinkImportLater") : Loc.Format("OpenLinkFormat", Loc.Get(kind)), IsLink: true));
            sender.ItemsSource = items;
            return;
        }
        try
        {
            await Task.Delay(250, cts.Token);
            var library = App.Services.GetRequiredService<Library>();
            var local = await Task.Run(() => library.SearchLibrary(text, 3), cts.Token);
            items.AddRange(local.Select(t => new SuggestionVm("", t.Title, $"{Loc.Get("InLibrary")} · {t.ArtistsText}", t)));
            sender.ItemsSource = items.ToList();
            var remote = await App.Services.GetRequiredService<YouTubeMusic>().SuggestionsAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            items.AddRange(remote.Select(q => new SuggestionVm("", q)));
            sender.ItemsSource = items;
        }
        catch (OperationCanceledException)
        {
        }
        catch (YouTubeException)
        {
            // Без сети подсказок нет — остаётся «В библиотеке»
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _suggestions?.Cancel();
        sender.IsSuggestionListOpen = false;
        if (args.ChosenSuggestion is SuggestionVm { Track: { } track })
        {
            App.Services.GetRequiredService<TrackActions>().Play([track], 0, new TrackContext.Single());
            return;
        }
        var query = (args.ChosenSuggestion is SuggestionVm { IsLink: false } suggestion ? suggestion.Text : args.QueryText).Trim();
        if (query.Length == 0) return;
        sender.Text = query;
        App.Services.GetRequiredService<LinkRouter>().OpenText(query);
    }
}
