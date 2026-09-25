using Melogold.App.Services;
using Melogold.Core.Domain;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// «Добавить устройство» (tasks/0006 §2, API §4.6, режим <c>request</c>): новое устройство (часы, где неудобно набирать
/// пароль) показывает код <c>K7QX-M2PD</c>; здесь его вводят, видят, какое устройство просится, и выбирают число,
/// которое видно на нём. Неверное число сервер считает отказом.
/// </summary>
public static class LinkDeviceDialog
{
    /// <summary>true — устройство вошло в аккаунт (список устройств стоит обновить).</summary>
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        var account = App.Services.GetRequiredService<AccountService>();
        var code = new TextBox { Header = Loc.Get("LinkCodeHeader"), PlaceholderText = "XXXX-XXXX", IsSpellCheckEnabled = false, CharacterCasing = CharacterCasing.Upper, MaxLength = 12 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 12, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = Loc.Get("LinkCodeText"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(code);
        body.Children.Add(error);
        body.Children.Add(progress);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("AccountAddDevice"),
            Content = body,
            PrimaryButtonText = Loc.Get("LinkContinue"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => code.Focus(FocusState.Programmatic);

        LinkDetails? link = null;
        string? approvedName = null;
        var busy = false;

        void Fail(string text)
        {
            error.Text = text;
            error.Visibility = Visibility.Visible;
        }

        void Busy(bool on)
        {
            busy = on;
            progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = !on;
        }

        // Ошибки API §2.2 — словами задания; лимит устройств закрывает окно: смотреть надо на список устройств
        string Explain(ApiException e) => e.Code switch
        {
            "link_not_found" => Loc.Get("LinkNotFound"),
            "link_expired" or "link_cancelled" => Loc.Get("LinkExpired"),
            "link_verify_mismatch" => Loc.Get("LinkVerifyMismatch"),
            "link_already_claimed" => Loc.Get("LinkAlreadyClaimed"),
            "link_wrong_mode" => Loc.Get("LinkWrongMode"),
            "link_not_claimed" => Loc.Get("LinkExpired"),
            _ => AccountTexts.Error(e),
        };

        async Task DecideAsync(string? verifyCode)
        {
            if (link is null || busy) return;
            Busy(true);
            error.Visibility = Visibility.Collapsed;
            try
            {
                if (verifyCode is null)
                {
                    await account.DenyLinkAsync(link.LinkId);
                    App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("LinkDenied"));
                }
                else
                {
                    await account.ApproveLinkAsync(link.LinkId, verifyCode);
                    approvedName = link.Device?.Name ?? Loc.Get("DeviceOther");
                }
                dialog.Hide();
            }
            catch (ApiException e) when (e.Code == "device_limit_reached")
            {
                App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("AccountErrorDeviceLimit"));
                dialog.Hide();
            }
            catch (ApiException e)
            {
                Fail(Explain(e));
                if (e.Code == "link_verify_mismatch") ShowResult(body, dialog);
            }
            finally
            {
                Busy(false);
            }
        }

        dialog.PrimaryButtonClick += async (_, e) =>
        {
            // Окно не закрывается: дальше — карточка устройства и выбор числа
            e.Cancel = true;
            if (busy || link is not null) return;
            if (UserCode.Normalize(code.Text) is not { } normalized)
            {
                Fail(Loc.Get("LinkInvalidCode"));
                return;
            }
            code.Text = normalized;
            Busy(true);
            error.Visibility = Visibility.Collapsed;
            try
            {
                link = await account.ResolveLinkAsync(normalized);
                ShowDevice(body, link, error, progress, async choice => await DecideAsync(choice));
                dialog.PrimaryButtonText = "";
                dialog.SecondaryButtonText = Loc.Get("LinkDeny");
            }
            catch (ApiException ex)
            {
                Fail(Explain(ex));
            }
            finally
            {
                Busy(false);
            }
        };
        dialog.SecondaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            await DecideAsync(null);
        };

        await dialog.ShowAsync();
        if (approvedName is null) return false;
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Format("LinkApprovedFormat", approvedName));
        return true;
    }

    /// <summary>Какое устройство просится: значок, имя, модель и система, та же ли сеть, сколько ещё действует код; три числа.</summary>
    private static void ShowDevice(StackPanel body, LinkDetails link, TextBlock error, ProgressBar progress, Action<string> choose)
    {
        body.Children.Clear();
        var device = link.Device;
        var card = new Grid { ColumnSpacing = 16, Padding = new Thickness(16), CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = DeviceSymbols.Glyph(device?.Platform), FontSize = 32, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(icon, DeviceTexts.Kind(device?.Platform));
        card.Children.Add(icon);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = device?.Name ?? Loc.Get("DeviceOther"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var details = string.Join(" · ", new[] { device?.Model, device?.OsVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (details.Length > 0) text.Children.Add(Secondary(details));
        if (link.SameNetwork is { } same) text.Children.Add(Secondary(Loc.Get(same ? "LinkSameNetwork" : "LinkOtherNetwork")));
        if (IsoTime.TryParse(link.ExpiresAt) is { } expires)
            text.Children.Add(Secondary(Loc.Format("LinkExpiresFormat", Math.Max(1, (int)Math.Ceiling((expires - IsoTime.NowMs()) / 60_000.0)))));
        Grid.SetColumn(text, 1);
        card.Children.Add(text);
        body.Children.Add(card);

        body.Children.Add(new TextBlock { Text = Loc.Get("LinkChooseNumber"), TextWrapping = TextWrapping.Wrap });
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var choice in link.VerifyChoices)
        {
            var button = new Button { Content = new TextBlock { Text = choice, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, MinWidth = 88, MinHeight = 64 };
            button.Click += (_, _) => choose(choice);
            numbers.Children.Add(button);
        }
        body.Children.Add(numbers);
        body.Children.Add(error);
        body.Children.Add(progress);
    }

    /// <summary>После «число не совпало» выбирать больше нечего: остаётся только закрыть.</summary>
    private static void ShowResult(StackPanel body, ContentDialog dialog)
    {
        foreach (var child in body.Children.OfType<StackPanel>()) child.Visibility = Visibility.Collapsed;
        dialog.SecondaryButtonText = "";
        dialog.CloseButtonText = Loc.Get("Close");
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };
}

/// <summary>Подпись вида устройства для экранного диктора (tasks/0006 §1).</summary>
public static class DeviceTexts
{
    public static string Kind(string? platform) => Loc.Get(DeviceSymbols.Kind(platform) switch
    {
        DeviceKind.Phone => "DevicePhone",
        DeviceKind.Tablet => "DeviceTablet",
        DeviceKind.Computer => "DeviceComputer",
        DeviceKind.Watch => "DeviceWatch",
        DeviceKind.Headset => "DeviceHeadset",
        _ => "DeviceOther",
    });
}
