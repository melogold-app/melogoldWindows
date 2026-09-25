using CommunityToolkit.WinUI.Controls;
using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Domain;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Melogold.App.Views;

/// <summary>Общая разметка экранов аккаунта: заголовок, пояснение, поля формы шириной как в «Параметрах».</summary>
internal static class Form
{
    public const double Width = 480;

    public static (ScrollViewer Scroller, StackPanel Body) Page(string title, string? description)
    {
        var body = new StackPanel { Margin = (Thickness)Application.Current.Resources["PageContentMargin"], Spacing = 12, MaxWidth = 1000 };
        body.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        if (description is not null) body.Children.Add(Secondary(description, Width + 120));
        return (new ScrollViewer { Content = body }, body);
    }

    public static TextBlock Secondary(string text, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = maxWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    public static TextBlock ErrorText() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = Width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        Visibility = Visibility.Collapsed,
    };

    public static void ShowError(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public static TextBox Login(string text = "", string? hint = null) => new()
    {
        Header = Loc.Get("AccountLogin"),
        Text = text,
        Description = hint,
        Width = Width,
        HorizontalAlignment = HorizontalAlignment.Left,
        IsSpellCheckEnabled = false,
        MaxLength = 32,
        InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.AlphanumericHalfWidth) } },
    };

    public static PasswordBox Password(string header, string? hint = null)
    {
        var box = new PasswordBox { Header = header, Width = Width, HorizontalAlignment = HorizontalAlignment.Left };
        if (hint is not null) box.Description = hint;
        return box;
    }

    /// <summary>Кнопка действия формы: на время запроса — кольцо вместо текста.</summary>
    public sealed partial class ActionButton : Button
    {
        private readonly TextBlock _text = new();
        private readonly ProgressRing _ring = new() { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };

        public ActionButton(string text, bool accent = true)
        {
            Text = text;
            var grid = new Grid();
            grid.Children.Add(_text);
            grid.Children.Add(_ring);
            Content = grid;
            MinWidth = 160;
            if (accent) Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        public string Text
        {
            get => _text.Text;
            set
            {
                _text.Text = value;
                // Содержимое — не строка: имя для экранного диктора и автоматизации задаётся явно
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, value);
            }
        }

        public bool Busy
        {
            get => _ring.IsActive;
            set
            {
                _ring.IsActive = value;
                _ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _text.Opacity = value ? 0 : 1;
            }
        }
    }

    public static AccountService Account => App.Services.GetRequiredService<AccountService>();

    public static Navigator Navigator => App.Services.GetRequiredService<Navigator>();
}

/// <summary>Вход логином и паролем (API §4.3, Android <c>SignInScreen</c>).</summary>
public sealed partial class SignInPage : Page
{
    private readonly TextBox _login;
    private readonly PasswordBox _password = Form.Password(Loc.Get("AccountPassword"));
    private readonly TextBlock _error = Form.ErrorText();
    private readonly Form.ActionButton _submit = new(Loc.Get("AccountSignIn"));

    public SignInPage()
    {
        InitializeComponent();
        var account = Form.Account;
        // Вход после того, как сервер закончил сессию: логин известен
        _login = Form.Login(account.State is AccountState.AuthRequired required ? required.Login : account.Session?.Login ?? "");
        var (scroller, body) = Form.Page(Loc.Get("AccountSignInTitle"), Loc.Get("AccountSignInText"));
        body.Children.Add(_login);
        body.Children.Add(_password);
        body.Children.Add(_error);
        body.Children.Add(_submit);
        var register = new HyperlinkButton { Content = Loc.Get("AccountNoAccount"), Padding = new Thickness(0, 4, 0, 4) };
        register.Click += (_, _) => Form.Navigator.Open(typeof(RegisterPage));
        body.Children.Add(register);
        body.Children.Add(Form.Caption(Loc.Format("AccountOnServerFormat", AccountTexts.Host(account.ServerUrl))));
        Content = scroller;

        _login.TextChanged += (_, _) => Update();
        _password.PasswordChanged += (_, _) => Update();
        _password.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            _ = SubmitAsync();
        };
        _submit.Click += (_, _) => _ = SubmitAsync();
        Update();
        Loaded += (_, _) => (_login.Text.Length == 0 ? (Control)_login : _password).Focus(FocusState.Programmatic);
    }

    private void Update() => _submit.IsEnabled = !_submit.Busy && _login.Text.Trim().Length > 0 && _password.Password.Length > 0;

    private async Task SubmitAsync()
    {
        if (_submit.Busy || !_submit.IsEnabled) return;
        _submit.Busy = true;
        Update();
        Form.ShowError(_error, null);
        try
        {
            await Form.Account.SignInAsync(_login.Text.Trim(), _password.Password);
            Form.Navigator.BackToRoot();
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Form.ShowError(_error, AccountTexts.Error(e));
        }
        finally
        {
            _submit.Busy = false;
            Update();
        }
    }
}

/// <summary>Регистрация логином и паролем, затем код восстановления — один раз (Android <c>RegisterScreen</c>).</summary>
public sealed partial class RegisterPage : Page
{
    private readonly TextBox _login = Form.Login(hint: Loc.Get("AccountLoginHint"));
    private readonly PasswordBox _password = Form.Password(Loc.Get("AccountPassword"), Loc.Get("AccountPasswordHint"));
    private readonly PasswordBox _repeat = Form.Password(Loc.Get("AccountPasswordRepeat"));
    private readonly TextBlock _error = Form.ErrorText();
    private readonly Form.ActionButton _submit = new(Loc.Get("AccountRegister"));

    public RegisterPage()
    {
        InitializeComponent();
        var (scroller, body) = Form.Page(Loc.Get("AccountRegisterTitle"), Loc.Get("AccountRegisterText"));
        body.Children.Add(_login);
        body.Children.Add(_password);
        body.Children.Add(_repeat);
        body.Children.Add(_error);
        body.Children.Add(_submit);
        body.Children.Add(Form.Caption(Loc.Format("AccountOnServerFormat", AccountTexts.Host(Form.Account.ServerUrl))));
        Content = scroller;

        _login.TextChanged += (_, _) => Update();
        _password.PasswordChanged += (_, _) => Update();
        _repeat.PasswordChanged += (_, _) => Update();
        _repeat.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            _ = SubmitAsync();
        };
        _submit.Click += (_, _) => _ = SubmitAsync();
        Update();
        Loaded += (_, _) => _login.Focus(FocusState.Programmatic);
    }

    private void Update() =>
        _submit.IsEnabled = !_submit.Busy && _login.Text.Trim().Length >= 3 && _password.Password.Length >= 8 && _repeat.Password.Length > 0;

    private async Task SubmitAsync()
    {
        if (_submit.Busy || !_submit.IsEnabled) return;
        if (_password.Password != _repeat.Password)
        {
            Form.ShowError(_error, Loc.Get("AccountErrorPasswordMismatch"));
            return;
        }
        _submit.Busy = true;
        Update();
        Form.ShowError(_error, null);
        try
        {
            // Доказательство работы считается в фоне (API §4.3): кнопка крутится, окно живое
            var code = await Form.Account.RegisterAsync(_login.Text.Trim(), _password.Password);
            ShowRecoveryCode(code);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Form.ShowError(_error, AccountTexts.Error(e));
        }
        finally
        {
            _submit.Busy = false;
            Update();
        }
    }

    /// <summary>Код восстановления: крупно, моноширинным, «Копировать» и «Я сохранил код» (REWRITE §3.5.13).</summary>
    private void ShowRecoveryCode(string code)
    {
        var (scroller, body) = Form.Page(Loc.Get("AccountRecoveryTitle"), Loc.Get("AccountRecoveryText"));
        var codeText = new TextBlock
        {
            // Строка переносится только между группами кода
            Text = code.Replace("-", "-​", StringComparison.Ordinal),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 24,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        body.Children.Add(new Border
        {
            Child = codeText,
            Width = Form.Width,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        });
        var copy = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 16 }, new TextBlock { Text = Loc.Get("AccountRecoveryCopy") } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, Loc.Get("AccountRecoveryCopy"));
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("AccountRecoveryCopied"));
        };
        body.Children.Add(copy);
        var saved = new CheckBox { Content = Loc.Get("AccountRecoverySaved") };
        body.Children.Add(saved);
        var done = new Button { Content = Loc.Get("Done"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 160, IsEnabled = false };
        saved.Checked += (_, _) => done.IsEnabled = true;
        saved.Unchecked += (_, _) => done.IsEnabled = false;
        done.Click += (_, _) => Form.Navigator.BackToRoot();
        body.Children.Add(done);
        Content = scroller;
    }
}

/// <summary>
/// Аккаунт (REWRITE §3.5.13, Android <c>AccountScreen</c>): «Синхронизировать сейчас» со статусом, устройства с
/// «Отвязать» и «Выйти на других устройствах», «Выйти».
/// </summary>
public sealed partial class AccountPage : Page
{
    private readonly AccountService _account = Form.Account;
    private readonly LibrarySync _sync = App.Services.GetRequiredService<LibrarySync>();
    private readonly ClickableCard _syncCard;
    private readonly StackPanel _devices = new() { Spacing = (double)Application.Current.Resources["SettingsCardSpacing"] };
    private readonly TextBlock _title;

    public AccountPage()
    {
        InitializeComponent();
        var (scroller, body) = Form.Page(_account.Session?.Login ?? Loc.Get("AccountSyncGroup"), null);
        body.Spacing = (double)Application.Current.Resources["SettingsCardSpacing"];
        _title = (TextBlock)body.Children[0];

        body.Children.Add(Header("AccountSyncGroup"));
        _syncCard = new ClickableCard { Header = Loc.Get("AccountSyncNow"), IsActionIconVisible = false, HeaderIcon = new FontIcon { Glyph = "" } };
        _syncCard.Activated += (_, _) => _ = _sync.SyncAsync(true);
        body.Children.Add(_syncCard);
        var what = Form.Caption(Loc.Get("AccountSyncWhat"));
        what.Margin = new Thickness(1, 4, 0, 0);
        body.Children.Add(what);

        body.Children.Add(Header("AccountDevicesGroup"));
        body.Children.Add(_devices);

        var signOut = new Button
        {
            Margin = new Thickness(0, 28, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 16 }, new TextBlock { Text = Loc.Get("AccountSignOut") } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(signOut, Loc.Get("AccountSignOut"));
        signOut.Click += async (_, _) => await SignOutAsync();
        body.Children.Add(signOut);
        Content = scroller;

        Loaded += (_, _) =>
        {
            _account.StateChanged += OnAccountChanged;
            _sync.StatusChanged += OnStatusChanged;
            _sync.DevicesChanged += OnDevicesChanged;
            ShowStatus();
            _ = LoadDevicesAsync();
        };
        Unloaded += (_, _) =>
        {
            _account.StateChanged -= OnAccountChanged;
            _sync.StatusChanged -= OnStatusChanged;
            _sync.DevicesChanged -= OnDevicesChanged;
        };
    }

    private static TextBlock Header(string key) => new() { Text = Loc.Get(key), Style = (Style)Application.Current.Resources["SettingsSectionHeaderStyle"] };

    // Вышли здесь или на другом устройстве: показывать нечего
    private void OnAccountChanged(AccountState state) => DispatcherQueue.TryEnqueue(() =>
    {
        if (state is not AccountState.SignedIn && ReferenceEquals(Frame?.Content, this)) Form.Navigator.BackToRoot();
    });

    private void OnStatusChanged(SyncStatus status) => DispatcherQueue.TryEnqueue(ShowStatus);

    private void OnDevicesChanged() => DispatcherQueue.TryEnqueue(() => _ = LoadDevicesAsync());

    private void ShowStatus()
    {
        _title.Text = _account.Session?.Login ?? _title.Text;
        _syncCard.Description = AccountTexts.Status(_sync.Status);
        _syncCard.HeaderIcon = new FontIcon { Glyph = _sync.Status is SyncStatus.Failed ? "" : "" };
        _syncCard.IsEnabled = _sync.Status is not SyncStatus.Syncing;
    }

    private async Task LoadDevicesAsync()
    {
        DeviceListResponse list;
        try
        {
            list = await _account.DevicesAsync();
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Log.Warn("Devices not loaded", e);
            return;
        }
        _devices.Children.Clear();
        foreach (var device in list.Devices.OrderByDescending(d => d.IsCurrent).ThenByDescending(d => IsoTime.TryParse(d.LastSeenAt) ?? 0))
        {
            var card = new SettingsCard
            {
                Header = device.Name,
                Description = device.IsCurrent ? Loc.Get("AccountDeviceCurrent")
                    : IsoTime.TryParse(device.LastSeenAt) is { } seen ? Loc.Format("AccountDeviceSeenFormat", AccountTexts.Relative(seen)) : "",
                HeaderIcon = new FontIcon { Glyph = device.Platform is "android" or "ios" ? "" : "" },
            };
            if (!device.IsCurrent)
            {
                var revoke = new Button { Content = Loc.Get("AccountDeviceRevoke") };
                revoke.Click += async (_, _) =>
                {
                    if (await PasswordConfirmAsync(Loc.Format("AccountDeviceRevokeTitleFormat", device.Name), Loc.Get("AccountDeviceRevokeText"), Loc.Get("AccountDeviceRevoke"),
                            password => _account.RevokeAsync(device.Id, password)))
                        await LoadDevicesAsync();
                };
                card.Content = revoke;
            }
            _devices.Children.Add(card);
        }
        if (list.Devices.Count > 1)
        {
            var others = new ClickableCard { Header = Loc.Get("AccountRevokeOthers"), IsActionIconVisible = false, HeaderIcon = new FontIcon { Glyph = "" } };
            others.Activated += async (_, _) =>
            {
                var count = 0;
                if (await PasswordConfirmAsync(Loc.Get("AccountRevokeOthersTitle"), Loc.Get("AccountRevokeOthersText"), Loc.Get("AccountRevokeOthers"),
                        async password => count = (await _account.RevokeOthersAsync(password)).RevokedCount))
                {
                    App.Services.GetRequiredService<Snackbar>().Show(Loc.Format("AccountRevokedOthersFormat", count));
                    await LoadDevicesAsync();
                }
            };
            _devices.Children.Add(others);
        }
    }

    /// <summary>
    /// Разрушительное действие с устройствами: сначала без пароля; если сервер просит его (устройство новое на аккаунте,
    /// DESIGN §4.8) — диалог спрашивает пароль и пробует снова.
    /// </summary>
    private async Task<bool> PasswordConfirmAsync(string title, string text, string confirm, Func<string?, Task> action)
    {
        var password = Form.Password(Loc.Get("AccountPassword"));
        password.Visibility = Visibility.Collapsed;
        password.Width = double.NaN;
        var error = Form.ErrorText();
        var content = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, password, error } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = confirm,
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        var needsPassword = false;
        password.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = password.Password.Length > 0;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            Form.ShowError(error, null);
            try
            {
                await action(needsPassword ? password.Password : null);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException)
            {
                args.Cancel = true;
                if (e is ApiException { Code: "recent_device_restricted" } && !needsPassword)
                {
                    needsPassword = true;
                    password.Visibility = Visibility.Visible;
                    dialog.PrimaryButtonText = Loc.Get("AccountPasswordConfirm");
                    dialog.IsPrimaryButtonEnabled = false;
                }
                Form.ShowError(error, AccountTexts.Error(e));
            }
            finally
            {
                deferral.Complete();
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task SignOutAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("AccountSignOutTitle"),
            Content = Loc.Get("AccountSignOutText"),
            PrimaryButtonText = Loc.Get("AccountSignOut"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _account.SignOutAsync();
    }
}

/// <summary>
/// Сервер (REWRITE §3.5.12, Android <c>ServerScreen</c>): адрес по <see cref="ServerAddressPolicy"/>, проверка
/// <c>/server/info</c> (это сервер Melogold, с которым приложение умеет говорить) и «Подключить», который выходит из
/// аккаунта прежнего сервера.
/// </summary>
public sealed partial class ServerPage : Page
{
    private readonly AccountService _account = Form.Account;
    private readonly TextBox _address = new() { Header = Loc.Get("ServerAddress"), Width = Form.Width, HorizontalAlignment = HorizontalAlignment.Left, IsSpellCheckEnabled = false };
    private readonly TextBlock _info = Form.Secondary("");
    private readonly TextBlock _error = Form.ErrorText();
    private readonly Form.ActionButton _check = new(Loc.Get("ServerCheck"), accent: false);
    private readonly Form.ActionButton _connect = new(Loc.Get("ServerConnect"));
    private readonly TextBlock _switchNote = Form.Caption(Loc.Get("ServerSwitchSignsOut"));
    private readonly HyperlinkButton _default = new() { Content = Loc.Get("ServerDefault"), Padding = new Thickness(0, 4, 0, 4) };
    private ServerInfo? _checkedInfo;
    private string? _checked;

    public ServerPage()
    {
        InitializeComponent();
        var (scroller, body) = Form.Page(Loc.Get("ServerTitle"), Loc.Get("ServerText"));
        _address.Text = _account.ServerUrl;
        body.Children.Add(_address);
        body.Children.Add(_info);
        body.Children.Add(_error);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _check, _connect } });
        body.Children.Add(_switchNote);
        body.Children.Add(_default);
        Content = scroller;

        _address.TextChanged += (_, _) =>
        {
            _checkedInfo = null;
            Form.ShowError(_error, null);
            Update();
        };
        _address.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            _ = CheckAsync(false);
        };
        _check.Click += (_, _) => _ = CheckAsync(false);
        _connect.Click += (_, _) => _ = CheckAsync(true);
        _default.Click += (_, _) => _address.Text = AccountService.DefaultServerUrl;
        Update();
        Loaded += (_, _) => _ = CheckAsync(false);
    }

    private string? Normalized => ServerAddressPolicy.Normalize(_address.Text) is ServerAddress.Valid valid ? valid.Url : null;

    private void Update()
    {
        var parsed = ServerAddressPolicy.Normalize(_address.Text);
        _address.Description = parsed switch
        {
            ServerAddress.Invalid { Code: not "empty" } invalid => Loc.Get(invalid.Code switch
            {
                "https_required" => "ServerHttpsNeeded",
                "credentials_or_params" => "ServerErrorParams",
                "unsupported_scheme" => "ServerErrorScheme",
                _ => "ServerErrorMalformed",
            }),
            ServerAddress.Valid { Insecure: true } => Loc.Get("ServerInsecure"),
            _ => null,
        };
        var normalized = Normalized;
        var current = normalized == _account.ServerUrl;
        var busy = _check.Busy || _connect.Busy;
        _check.IsEnabled = !busy && normalized is not null;
        _connect.IsEnabled = !busy && normalized is not null && !current;
        _connect.Text = Loc.Get(current ? "ServerCurrent" : "ServerConnect");
        _switchNote.Visibility = !current && _account.Session is not null ? Visibility.Visible : Visibility.Collapsed;
        _default.Visibility = _address.Text.Trim() == AccountService.DefaultServerUrl ? Visibility.Collapsed : Visibility.Visible;
        if (_checkedInfo is { } info && _checked == normalized)
        {
            _info.Text = Loc.Format("ServerInfoFormat", info.InstanceName, info.Version) + "\n" + Loc.Get(info.Registration switch
            {
                "open" => "ServerRegistrationOpen",
                "first" => "ServerRegistrationFirst",
                _ => "ServerRegistrationClosed",
            });
            _info.Visibility = Visibility.Visible;
        }
        else _info.Visibility = Visibility.Collapsed;
    }

    private async Task CheckAsync(bool connect)
    {
        if (Normalized is not { } url || _check.Busy || _connect.Busy) return;
        var button = connect ? _connect : _check;
        button.Busy = true;
        Form.ShowError(_error, null);
        Update();
        try
        {
            _checkedInfo = await _account.CheckAsync(url);
            _checked = url;
            if (connect)
            {
                _account.SetServer(url);
                App.Services.GetRequiredService<SettingsStore>().ServerUrl = url == AccountService.DefaultServerUrl ? null : url;
                Form.Navigator.GoBack();
            }
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            _checkedInfo = null;
            Form.ShowError(_error, e is ApiException { Code: "not_melogold" } ? Loc.Get("ServerNotMelogold")
                : e is ApiException { Code: "client_outdated" or "server_outdated" } ? AccountTexts.Error(e) : Loc.Get("ServerUnreachable"));
        }
        finally
        {
            button.Busy = false;
            Update();
        }
    }
}
