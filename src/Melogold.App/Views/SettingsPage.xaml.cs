using System.Diagnostics;
using Melogold.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

/// <summary>Настройки — как «Параметры» Windows 11 (§5.4): карточки <c>SettingsCard</c>, только то, что есть на Android.</summary>
public sealed partial class SettingsPage : Page, IScrollToTop
{
    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        VersionCard.Description = Loc.Format("VersionFormat", AppInfo.Version);
        _ready = true;
    }

    public void ScrollToTop() => Scroller.ChangeView(null, 0, null);

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = (AppTheme)ThemeBox.SelectedIndex;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Open(AppPaths.Logs);

    private void OnOpenSource(object sender, RoutedEventArgs e) => Open(AppInfo.RepositoryUrl);

    private static void Open(string target)
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
