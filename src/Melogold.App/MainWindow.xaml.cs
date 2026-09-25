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

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "melogold.ico"));
        ApplyBackdrop();
        ApplyTheme();
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
        NowPlaying.OpenChanged += _ => UpdateChrome();
        _navigator.Show(_settings.LastSection);

        var player = App.Services.GetRequiredService<PlayerViewModel>();
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, FocusSearch));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigator.GoBack()));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Escape, VirtualKeyModifiers.None, GoBack));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Right, VirtualKeyModifiers.Control, () => player.Engine.Next()));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Left, VirtualKeyModifiers.Control, () => player.Engine.Previous()));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Up, VirtualKeyModifiers.Control, () => player.ChangeVolume(5)));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Down, VirtualKeyModifiers.Control, () => player.ChangeVolume(-5)));
        Root.KeyDown += OnRootKeyDown;
        Root.PointerPressed += OnRootPointerPressed;

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

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return accelerator;
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

    /// <summary>«Назад» и Esc сначала закрывают «Сейчас играет».</summary>
    private void GoBack()
    {
        if (NowPlaying.IsOpen) NowPlaying.Close();
        else _navigator.GoBack();
    }

    // ---------- Клавиатура и мышь ----------

    private bool FocusInTextInput() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox or RichEditBox;

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusInTextInput()) return;
        // «/» — поиск, пробел — play/pause, если фокус не на кнопке или строке (§5.5)
        if (e.Key == (VirtualKey)191)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space && FocusManager.GetFocusedElement(Content.XamlRoot) is not (ButtonBase or ToggleSwitch or ListViewItem))
        {
            App.Services.GetRequiredService<PlayerViewModel>().Engine.TogglePlayPause();
            e.Handled = true;
        }
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
        var recent = App.Services.GetRequiredService<Library>().RecentSearches(8);
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
