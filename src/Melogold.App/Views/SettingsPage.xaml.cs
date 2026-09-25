using System.Diagnostics;
using Melogold.App.Services;
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
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        VersionCard.Description = Loc.Format("VersionFormat", AppInfo.Version);
        SignInButton.Content = Loc.Get("AccountSignIn");
        RegisterButton.Content = Loc.Get("AccountRegister");
        _account.StateChanged += _ => DispatcherQueue.TryEnqueue(ShowAccount);
        _sync.StatusChanged += _ => DispatcherQueue.TryEnqueue(ShowAccount);
        // «2 минуты назад» стареет: при каждом показе — заново
        Loaded += (_, _) => ShowAccount();
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

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = (AppTheme)ThemeBox.SelectedIndex;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenExternal(AppPaths.Logs);

    private void OnOpenSource(object sender, RoutedEventArgs e) => OpenExternal(AppInfo.RepositoryUrl);

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
