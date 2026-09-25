using System.Diagnostics;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

/// <summary>Настройки — как «Параметры» Windows 11 (§5.4): карточки <c>SettingsCard</c>, только то, что есть на Android.</summary>
public sealed partial class SettingsPage : Page, IScrollToTop
{
    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private readonly AccountService _account = App.Services.GetRequiredService<AccountService>();
    private readonly LibrarySync _sync = App.Services.GetRequiredService<LibrarySync>();
    private readonly UpdateService _updates = App.Services.GetRequiredService<UpdateService>();
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        Controls.PageColumn.Center(Scroller, Column, 1000);
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        PauseHistorySwitch.IsOn = _settings.PauseHistory;
        PauseSearchSwitch.IsOn = _settings.PauseSearchHistory;
        UpdateButton.Content = Loc.Get("UpdateAction");
        WhatsNewButton.Content = Loc.Get("UpdateWhatsNew");
        _updates.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowUpdate);
        ShowUpdate();
        // Скорость 0,5–2× — одна на все треки (§4)
        foreach (var speed in Speeds) SpeedBox.Items.Add(new ComboBoxItem { Content = speed == 1 ? Loc.Get("SpeedNormal") : $"{speed.ToString(System.Globalization.CultureInfo.CurrentCulture)}×" });
        SpeedBox.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _settings.Speed));
        NormalizeSwitch.IsOn = _settings.NormalizeVolume;
        SignInButton.Content = Loc.Get("AccountSignIn");
        RegisterButton.Content = Loc.Get("AccountRegister");
        _account.StateChanged += _ => DispatcherQueue.TryEnqueue(ShowAccount);
        _sync.StatusChanged += _ => DispatcherQueue.TryEnqueue(ShowAccount);
        // «2 минуты назад» стареет, кэш растёт: при каждом показе — заново
        Loaded += (_, _) =>
        {
            ShowAccount();
            _ = ShowStorageAsync();
        };
        ShowAccount();
        _ready = true;
    }

    /// <summary>
    /// Без аккаунта — «Melogold работает без аккаунта» с «Войти» и «Создать аккаунт»; с аккаунтом — кто и как идёт
    /// синхронизация (нажатие открывает аккаунт); после того как сервер закончил сессию — «Нужно войти снова».
    /// </summary>
    private void ShowAccount()
    {
        switch (_account.State)
        {
            case AccountState.SignedIn signedIn:
                AccountCard.Header = Loc.Format("AccountSignedInAsFormat", signedIn.Login);
                AccountCard.Description = AccountTexts.Status(_sync.Status);
                AccountIcon.Glyph = _sync.Status is SyncStatus.Failed ? "" : "";
                AccountCard.IsClickEnabled = true;
                AccountButtons.Visibility = Visibility.Collapsed;
                break;
            case AccountState.AuthRequired:
                AccountCard.Header = Loc.Get("AccountAuthRequiredTitle");
                AccountCard.Description = Loc.Get("AccountAuthRequiredText");
                AccountIcon.Glyph = "";
                AccountCard.IsClickEnabled = false;
                AccountButtons.Visibility = Visibility.Visible;
                RegisterButton.Visibility = Visibility.Collapsed;
                break;
            default:
                AccountCard.Header = Loc.Get("NoAccountTitle");
                AccountCard.Description = Loc.Get("NoAccountText");
                AccountIcon.Glyph = "";
                AccountCard.IsClickEnabled = false;
                AccountButtons.Visibility = Visibility.Visible;
                RegisterButton.Visibility = Visibility.Visible;
                break;
        }
        ServerCard.Description = AccountTexts.Host(_account.ServerUrl);
        // «Сведения о потоке» — когда что-то играет
        StreamInfoCard.IsEnabled = App.Services.GetRequiredService<Melogold.Playback.PlayerEngine>().Stream is not null;
    }

    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (_account.State is AccountState.SignedIn) Open(typeof(AccountPage));
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => Open(typeof(SignInPage));

    private void OnRegister(object sender, RoutedEventArgs e) => Open(typeof(RegisterPage));

    private void OnServerClick(object sender, RoutedEventArgs e) => Open(typeof(ServerPage));

    private static void Open(Type page) => App.Services.GetRequiredService<Navigator>().Open(page);

    public void ScrollToTop() => Scroller.ChangeView(null, 0, null);

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

    // ---------- Обновления (§3) ----------

    /// <summary>Карточка «Вышла новая версия» вверху и строка версии в «О приложении».</summary>
    private void ShowUpdate()
    {
        var state = _updates.State;
        var available = _updates.Available;
        UpdateCard.Visibility = available is null ? Visibility.Collapsed : Visibility.Visible;
        if (available is not null)
        {
            UpdateCard.Header = Loc.Format("UpdateAvailableTitle", available.Version);
            var size = UpdateService.Asset(available)?.SizeBytes ?? 0;
            UpdateCard.Description = state switch
            {
                UpdateState.Downloading => Loc.Format("UpdateDownloadingFormat", _updates.Progress),
                UpdateState.Installing => Loc.Get("UpdateInstalling"),
                UpdateState.Failed => Loc.Get("UpdateFailed"),
                _ => Loc.Format("UpdateAvailableText", FormatSize(size)),
            };
            UpdateProgress.Visibility = state == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = _updates.Progress;
            UpdateButton.IsEnabled = state is not (UpdateState.Downloading or UpdateState.Installing);
            WhatsNewButton.Visibility = available.LocalizedNotes is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        }
        VersionCard.Description = state switch
        {
            UpdateState.Disabled => Loc.Format("UpdateDisabledFormat", AppInfo.Version),
            UpdateState.Checking => Loc.Get("UpdateChecking"),
            UpdateState.UpToDate => Loc.Format("UpdateUpToDateFormat", AppInfo.Version),
            UpdateState.Failed when available is null => Loc.Format("UpdateOfflineFormat", AppInfo.Version),
            _ when available is not null => Loc.Format("UpdateVersionNewFormat", AppInfo.Version, available.Version),
            _ => Loc.Format("VersionFormat", AppInfo.Version),
        };
        CheckUpdatesButton.Visibility = state == UpdateState.Disabled ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesButton.IsEnabled = state is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing);
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await _updates.CheckAsync(force: true);

    private async void OnUpdate(object sender, RoutedEventArgs e) => await _updates.InstallAsync();

    private async void OnWhatsNew(object sender, RoutedEventArgs e)
    {
        if (_updates.Available is not { } available) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("UpdateWhatsNewTitle", available.Version),
            Content = new ScrollViewer { MaxHeight = 400, Content = new TextBlock { Text = available.LocalizedNotes ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            PrimaryButtonText = Loc.Get("UpdateAction"),
            CloseButtonText = Loc.Get("Close"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _updates.InstallAsync();
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? Loc.Format("SizeMegabytesFormat", (bytes / 1024.0 / 1024).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
        : Loc.Format("SizeKilobytesFormat", Math.Max(1, bytes / 1024));

    // ---------- Библиотека и история ----------

    private void OnPauseHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _settings.PauseHistory = PauseHistorySwitch.IsOn;
    }

    private void OnPauseSearchToggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _settings.PauseSearchHistory = PauseSearchSwitch.IsOn;
    }

    private async void OnClearSearches(object sender, RoutedEventArgs e)
    {
        await Task.Run(_library.ClearSearches);
        await ShowStorageAsync();
    }

    private async void OnResetHidden(object sender, RoutedEventArgs e)
    {
        await Task.Run(_library.ClearHiddenTracks);
        await ShowStorageAsync();
    }

    // ---------- Хранилище и данные ----------

    /// <summary>Сколько занято кэшем, есть ли история поиска и скрытые треки.</summary>
    private async Task ShowStorageAsync()
    {
        var (cache, searches, hidden) = await Task.Run(() => (CacheSize(), _library.RecentSearches(1).Count, _library.HiddenTracks().Count));
        CacheCard.Description = Loc.Format("CacheUsedFormat", FormatSize(cache));
        ClearCacheButton.IsEnabled = cache > 0;
        SearchHistoryCard.Description = searches == 0 ? Loc.Get("EmptySearchHistory") : null!;
        ClearSearchesButton.IsEnabled = searches > 0;
        HiddenCard.Description = hidden == 0 ? Loc.Get("BlacklistEmpty") : Loc.Plural("Tracks", hidden);
        ResetHiddenButton.IsEnabled = hidden > 0;
    }

    /// <summary>Кэш — файлы папки cache и найденные в сети тексты (свои и импортированные тексты — не кэш).</summary>
    private long CacheSize()
    {
        long files = 0;
        if (Directory.Exists(AppPaths.Cache))
            files = new DirectoryInfo(AppPaths.Cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return files + _library.FetchedLyricsSize();
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(AppPaths.Cache))
            {
                foreach (var file in new DirectoryInfo(AppPaths.Cache).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            _library.ClearFetchedLyrics();
        });
        App.Services.GetRequiredService<CatalogCache>().Clear();
        await ShowStorageAsync();
    }

    // ---------- Лицензии ----------

    private async void OnLicenses(object sender, RoutedEventArgs e)
    {
        var text = string.Join("\n\n",
            "Melogold — GNU GPL 3.0",
            ".NET, Windows App SDK, WinUI 3 — MIT",
            "CommunityToolkit.Mvvm, CommunityToolkit.WinUI — MIT",
            "Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite — MIT",
            "SQLite — public domain",
            "MaterialColorUtilities (albi005) — Apache 2.0",
            Loc.Get("LicensesServices"));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("LicensesTitle"),
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            CloseButtonText = Loc.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SpeedBox.SelectedIndex < 0) return;
        _settings.Speed = Speeds[SpeedBox.SelectedIndex];
        App.Services.GetRequiredService<Melogold.Playback.PlayerEngine>().ApplyVolume();
    }

    private void OnNormalizeToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.NormalizeVolume = NormalizeSwitch.IsOn;
        App.Services.GetRequiredService<Melogold.Playback.PlayerEngine>().ApplyVolume();
    }

    private async void OnStreamInfo(object sender, RoutedEventArgs e) =>
        await Controls.StreamInfoDialog.ShowAsync(XamlRoot, App.Services.GetRequiredService<Melogold.Playback.PlayerEngine>());

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = (AppTheme)ThemeBox.SelectedIndex;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenExternal(AppPaths.Logs);

    private void OnOpenSource(object sender, RoutedEventArgs e) => OpenExternal(AppInfo.RepositoryUrl);

    private void OnReportBug(object sender, RoutedEventArgs e) => OpenExternal($"{AppInfo.RepositoryUrl}/issues/new?labels=bug");

    private void OnRequestFeature(object sender, RoutedEventArgs e) => OpenExternal($"{AppInfo.RepositoryUrl}/issues/new?labels=enhancement");

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Cannot open {target}", error);
        }
    }
}
