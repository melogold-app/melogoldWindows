using System.Diagnostics;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Playback;
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
    private readonly ImageCache _images = App.Services.GetRequiredService<ImageCache>();
    private readonly SongCache _songs = App.Services.GetRequiredService<SongCache>();
    private bool _ready;

    /// <summary>«Максимальный размер», МБ — варианты Android (<c>CoilDiskCacheSize</c>, <c>ExoPlayerDiskCacheSize</c>); 0 — без ограничений.</summary>
    private static readonly long[] ImageCacheSizes = [64, 128, 256, 512, 1024, 2048];

    private static readonly long[] SongCacheSizes = [32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 0];

    public SettingsPage()
    {
        InitializeComponent();
        Controls.PageColumn.Center(Scroller, Column, 1000);
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        PauseHistorySwitch.IsOn = _settings.PauseHistory;
        PauseSearchSwitch.IsOn = _settings.PauseSearchHistory;
        UpdateButton.Content = VersionUpdateButton.Content = Loc.Get("UpdateAction");
        WhatsNewButton.Content = VersionWhatsNewButton.Content = Loc.Get("UpdateWhatsNew");
        _updates.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowUpdate);
        ShowUpdate();
        // Скорость 0,5–2× — одна на все треки (§4)
        foreach (var speed in Speeds) SpeedBox.Items.Add(new ComboBoxItem { Content = speed == 1 ? Loc.Get("SpeedNormal") : $"{speed.ToString(System.Globalization.CultureInfo.CurrentCulture)}×" });
        SpeedBox.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _settings.Speed));
        NormalizeSwitch.IsOn = _settings.NormalizeVolume;
        FillSizes(ImageCacheSizeBox, ImageCacheSizes, _settings.ImageCacheMaxMb, 128);
        FillSizes(SongCacheSizeBox, SongCacheSizes, _settings.SongCacheMaxMb, 2048);
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
        // Нашлась версия — в этой же карточке «Что нового» и «Обновить» вместо «Проверить обновления»
        var busy = state is UpdateState.Downloading or UpdateState.Installing;
        CheckUpdatesButton.Visibility = state == UpdateState.Disabled || available is not null ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesButton.IsEnabled = state is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing);
        CheckUpdatesButton.Content = state == UpdateState.Checking ? Loc.Get("UpdateChecking") : Loc.Get("CheckUpdates");
        VersionUpdateButton.Visibility = available is not null ? Visibility.Visible : Visibility.Collapsed;
        VersionUpdateButton.IsEnabled = !busy;
        VersionWhatsNewButton.Visibility = available?.LocalizedNotes is { Length: > 0 } && !busy ? Visibility.Visible : Visibility.Collapsed;
        VersionProgress.Visibility = state == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        VersionProgress.Value = _updates.Progress;
        if (available is not null && busy) VersionCard.Description = state == UpdateState.Downloading ? Loc.Format("UpdateDownloadingFormat", _updates.Progress) : Loc.Get("UpdateInstalling");
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

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? Loc.Format("SizeGigabytesFormat", (bytes / 1024.0 / 1024 / 1024).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
        : bytes >= 1024 * 1024
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

    /// <summary>Сколько занято кэшами, есть ли история поиска и скрытые треки.</summary>
    private async Task ShowStorageAsync()
    {
        var (cache, images, songs, searches, hidden) = await Task.Run(() => (CacheSize(), _images.Size, _songs.Size, _library.RecentSearches(1).Count, _library.HiddenTracks().Count));
        CacheCard.Description = Loc.Format("CacheUsedFormat", FormatSize(cache));
        ImageCacheCard.Description = Used(images, _settings.ImageCacheMaxMb);
        ClearImagesButton.IsEnabled = images > 0;
        SongCacheCard.Description = Used(songs, _settings.SongCacheMaxMb);
        ClearCacheButton.IsEnabled = cache > 0;
        SearchHistoryCard.Description = searches == 0 ? Loc.Get("EmptySearchHistory") : null!;
        ClearSearchesButton.IsEnabled = searches > 0;
        HiddenCard.Description = hidden == 0 ? Loc.Get("BlacklistEmpty") : Loc.Plural("Tracks", hidden);
        ResetHiddenButton.IsEnabled = hidden > 0;
    }

    /// <summary>«12 МБ использовано (9%)»; без ограничения — без процента (Android <c>CacheUsageEntry</c>).</summary>
    private static string Used(long bytes, long maxMb) => maxMb > 0
        ? Loc.Format("CacheUsedPercentFormat", FormatSize(bytes), Math.Min(100, bytes * 100 / (maxMb * 1024 * 1024)))
        : Loc.Format("CacheUsedPlainFormat", FormatSize(bytes));

    private static void FillSizes(ComboBox box, long[] sizes, long current, long fallback)
    {
        foreach (var mb in sizes) box.Items.Add(new ComboBoxItem { Content = mb == 0 ? Loc.Get("CacheUnlimited") : FormatSize(mb * 1024 * 1024), Tag = mb });
        box.SelectedIndex = Array.IndexOf(sizes, current) is var index and >= 0 ? index : Array.IndexOf(sizes, fallback);
    }

    private async void OnImageCacheSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ImageCacheSizeBox.SelectedItem is not ComboBoxItem { Tag: long mb }) return;
        _settings.ImageCacheMaxMb = mb;
        await Task.Run(_images.Trim);
        await ShowStorageAsync();
    }

    private async void OnSongCacheSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SongCacheSizeBox.SelectedItem is not ComboBoxItem { Tag: long mb }) return;
        _settings.SongCacheMaxMb = mb;
        await Task.Run(_songs.Trim);
        await ShowStorageAsync();
    }

    private async void OnClearImages(object sender, RoutedEventArgs e)
    {
        await Task.Run(_images.Clear);
        await ShowStorageAsync();
    }

    /// <summary>Кэш изображений и песен — своими карточками; здесь всё прочее в cache и найденные в сети тексты.</summary>
    private static IEnumerable<FileInfo> OtherCacheFiles() =>
        Directory.Exists(AppPaths.Cache)
            ? new DirectoryInfo(AppPaths.Cache).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.FullName.StartsWith(ImageCache.Directory, StringComparison.OrdinalIgnoreCase) && !f.FullName.StartsWith(Path.Combine(AppPaths.Cache, "songs"), StringComparison.OrdinalIgnoreCase))
            : [];

    /// <summary>Кэш — файлы папки cache и найденные в сети тексты (свои и импортированные тексты — не кэш).</summary>
    private long CacheSize() => OtherCacheFiles().Sum(f => f.Length) + _library.FetchedLyricsSize();

    // ---------- База данных ----------

    /// <summary>«Резервное копирование»: база — в файл, который выберет человек.</summary>
    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary, SuggestedFileName = DatabaseBackup.SuggestedName };
        picker.FileTypeChoices.Add(Loc.Get("BackupFileType"), [".db"]);
        if (App.Current?.Window is not { } window) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var snackbar = App.Services.GetRequiredService<Snackbar>();
        try
        {
            await Task.Run(() => DatabaseBackup.Export(App.Services.GetRequiredService<LibraryDatabase>(), file.Path));
            snackbar.Show(Loc.Get("BackupSaved"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Warn("Backup failed", ex);
            snackbar.Show(Loc.Get("BackupFailed"));
        }
    }

    /// <summary>«Восстановить»: база из файла заменит текущую; Melogold перезапускается, и она встаёт на место при запуске.</summary>
    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".db");
        if (App.Current?.Window is not { } window) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var snackbar = App.Services.GetRequiredService<Snackbar>();
        if (!await Task.Run(() => DatabaseBackup.IsValid(file.Path)))
        {
            snackbar.Show(Loc.Get("RestoreInvalid"));
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("RestoreTitle"),
            Content = new TextBlock { Text = Loc.Get("RestoreText"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("RestoreAction"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            DatabaseBackup.Schedule(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Restore failed", ex);
            snackbar.Show(Loc.Get("BackupFailed"));
            return;
        }
        // Правки последних секунд — на сервер, пока база прежняя
        _sync.Flush(TimeSpan.FromSeconds(3));
        Log.Info("Restarting to restore the database");
        var failure = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        Log.Warn($"Restart failed: {failure}", null);
        snackbar.Show(Loc.Get("RestoreRestartManually"));
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(AppPaths.Cache))
            {
                foreach (var file in OtherCacheFiles().ToList())
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
