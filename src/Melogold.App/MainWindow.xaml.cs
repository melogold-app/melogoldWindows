using Melogold.App.Services;
using Melogold.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Melogold.App;

public sealed partial class MainWindow : Window
{
    private readonly Navigator _navigator;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, Frame> _frames = [];

    private static readonly Section[] Sections =
    [
        new("trends", typeof(TrendsPage)),
        new("new", typeof(NewPage)),
        new("library", typeof(LibraryPage)),
        new("settings", typeof(SettingsPage)),
    ];

    public MainWindow()
    {
        InitializeComponent();
        _navigator = App.Services.GetRequiredService<Navigator>();
        _settings = App.Services.GetRequiredService<SettingsStore>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "melogold.ico"));
        ApplyBackdrop();
        ApplyTheme();
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
        _navigator.Show(_settings.LastSection);

        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, FocusSearch));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigator.GoBack()));
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => _navigator.GoBack()));
        Root.KeyDown += OnRootKeyDown;
        Root.PointerPressed += OnRootPointerPressed;

        // Размер в эффективных пикселях: при масштабе 150 % окно не должно выйти маленьким
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(820 * scale)));
        Closed += (_, _) => _settings.LastSection = _navigator.Current;
    }

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

    public Navigator Navigator => _navigator;

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
        AppTitleBar.IsBackButtonEnabled = _navigator.CanGoBack;
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
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

    private void OnTitleBarBackRequested(TitleBar sender, object args) => _navigator.GoBack();

    // ---------- Клавиатура и мышь ----------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // «/» — поиск, если фокус не в поле ввода (§5.5)
        if (e.Key == (VirtualKey)191 && FocusManager.GetFocusedElement(Content.XamlRoot) is not (TextBox or AutoSuggestBox or PasswordBox))
        {
            FocusSearch();
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

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    // ---------- Поиск ----------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (query.Length == 0) return;
        App.Services.GetRequiredService<LinkRouter>().OpenText(query);
    }
}
