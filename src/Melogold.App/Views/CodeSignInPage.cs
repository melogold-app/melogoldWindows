using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Domain;
using Melogold.Server;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Views;

/// <summary>
/// «Вход по коду» (tasks/0014, API §4.6) — новое устройство без пароля. Сразу показывает свой код (режим
/// <c>request</c>): его вводят на устройстве, где уже вошли (Аккаунт › Добавить устройство). «У меня есть код с другого
/// устройства» — наоборот (режим <c>invite</c>). Дальше в обоих — число, которое нужно выбрать там, и вход как по паролю.
/// Уход со страницы бросает код и на сервере.
/// </summary>
public sealed partial class CodeSignInPage : Page
{
    private readonly NewDeviceLinker _linker = new(new AccountLinkPort(Form.Account));
    private readonly StackPanel _area = new() { Spacing = 12, MaxWidth = Form.Width + 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _countdown = Form.Caption("");
    private readonly DispatcherQueueTimer _clock;
    private readonly TextBox _code = new() { Header = Loc.Get("LinkCodeHeader"), PlaceholderText = "XXXX-XXXX", IsSpellCheckEnabled = false, CharacterCasing = CharacterCasing.Upper, MaxLength = 12, Width = Form.Width, HorizontalAlignment = HorizontalAlignment.Left };
    private long _expiresAt;

    /// <summary>Вводим чужой код (invite), а не показываем свой.</summary>
    private bool _entering;

    public CodeSignInPage()
    {
        InitializeComponent();
        var (scroller, body) = Form.Page(Loc.Get("CodeSignInTitle"), null);
        body.Children.Add(_area);
        Content = scroller;
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ShowCountdown();
        _linker.StateChanged += state => DispatcherQueue.TryEnqueue(() => Render(state));
        Loaded += (_, _) =>
        {
            if (_linker.State is NewDeviceLinkState.Idle && !_entering) _ = _linker.ShowCodeAsync();
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _linker.Cancel();
        };
        _code.TextChanged += (_, _) => RenderEntryButton();
    }

    private Button? _continue;

    private void Render(NewDeviceLinkState state)
    {
        _area.Children.Clear();
        _clock.Stop();
        switch (state)
        {
            case NewDeviceLinkState.Starting:
                Waiting(Loc.Get(_entering ? "CodeSigningIn" : "CodeGetting"));
                break;
            case NewDeviceLinkState.ShowingCode code:
                _area.Children.Add(Form.Secondary(Loc.Get("CodeSignInText"), Form.Width + 120));
                _area.Children.Add(Tonal(Big(code.UserCode, 45, FontWeights.SemiBold, mono: true, Loc.Format("CodeSpokenFormat", LinkRules.SpokenCode(code.UserCode)))));
                Live(code.ExpiresAt, code.Reconnecting);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(CancelButton());
                buttons.Children.Add(Link(Loc.Get("CodeHaveOther"), () =>
                {
                    _entering = true;
                    _linker.Cancel();
                }));
                _area.Children.Add(buttons);
                break;
            case NewDeviceLinkState.Verify verify:
                var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                var icon = new FontIcon { Glyph = DeviceSymbols.Glyph(verify.ApproverPlatform), FontSize = 24 };
                AutomationProperties.SetName(icon, DeviceTexts.Kind(verify.ApproverPlatform));
                who.Children.Add(icon);
                who.Children.Add(new TextBlock { Text = Loc.Format("CodeChooseFormat", verify.ApproverName), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                _area.Children.Add(who);
                _area.Children.Add(Tonal(Big(verify.VerifyCode, 120, FontWeights.Bold, mono: false, Loc.Format("CodeNumberSpokenFormat", verify.VerifyCode))));
                _area.Children.Add(Form.Secondary(Loc.Format("CodeAccountFormat", verify.Login)));
                Live(verify.ExpiresAt, verify.Reconnecting);
                _area.Children.Add(CancelButton());
                break;
            case NewDeviceLinkState.SignedIn:
                // Как после входа по паролю: сессия уже в хранилище, первый синк пойдёт сам
                Form.Navigator.BackToRoot();
                break;
            case NewDeviceLinkState.Failed failed when _entering && !failed.Started:
                Entry(ErrorText(failed.Failure));
                break;
            case NewDeviceLinkState.Failed failed:
                var error = Form.ErrorText();
                Form.ShowError(error, ErrorText(failed.Failure));
                _area.Children.Add(error);
                var again = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var retry = new Button { Content = Loc.Get(_entering ? "CodeEnterOther" : failed.Started ? "CodeGetNew" : "Retry"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                retry.Click += (_, _) =>
                {
                    if (_entering) Entry(null);
                    else _ = _linker.ShowCodeAsync();
                };
                again.Children.Add(retry);
                again.Children.Add(Link(Loc.Get("CodeUsePassword"), () => Form.Navigator.GoBack()));
                _area.Children.Add(again);
                break;
            case NewDeviceLinkState.Idle when _entering:
                Entry(null);
                break;
        }
    }

    /// <summary>Режим invite: поле кода с подсказкой, «Продолжить» — только для настоящего кода; ввод не стирается.</summary>
    private void Entry(string? error)
    {
        _area.Children.Clear();
        _area.Children.Add(Form.Secondary(Loc.Get("CodeEnterHint"), Form.Width + 120));
        _area.Children.Add(_code);
        var errorText = Form.ErrorText();
        Form.ShowError(errorText, error);
        _area.Children.Add(errorText);
        _continue = new Button { Content = Loc.Get("LinkContinue"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _continue.Click += (_, _) => Claim();
        _code.KeyDown += OnCodeKey;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_continue);
        buttons.Children.Add(Link(Loc.Get("CodeShowMine"), () =>
        {
            _entering = false;
            _ = _linker.ShowCodeAsync();
        }));
        _area.Children.Add(buttons);
        RenderEntryButton();
        _code.Focus(FocusState.Programmatic);
    }

    private void OnCodeKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Claim();
    }

    private void RenderEntryButton()
    {
        if (_continue is not null) _continue.IsEnabled = UserCode.Normalize(_code.Text) is not null;
    }

    private void Claim()
    {
        if (UserCode.Normalize(_code.Text) is not { } normalized) return;
        _code.KeyDown -= OnCodeKey;
        _code.Text = normalized;
        _ = _linker.ClaimAsync(normalized);
    }

    private void Waiting(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        _area.Children.Add(row);
    }

    /// <summary>«Действует 4:32», «нет связи» и тонкий индикатор ожидания.</summary>
    private void Live(long expiresAt, bool reconnecting)
    {
        _expiresAt = expiresAt;
        ShowCountdown();
        _area.Children.Add(_countdown);
        if (reconnecting)
        {
            var offline = Form.ErrorText();
            Form.ShowError(offline, Loc.Get("CodeReconnecting"));
            _area.Children.Add(offline);
        }
        _area.Children.Add(new ProgressBar { IsIndeterminate = true, Width = Form.Width, HorizontalAlignment = HorizontalAlignment.Left });
        _clock.Start();
    }

    // Отсчёт — не живая область: экранный диктор не читает его каждую секунду
    private void ShowCountdown() => _countdown.Text = Loc.Format("CodeValidFormat", LinkRules.CountdownText(_expiresAt - IsoTime.NowMs()));

    private Button CancelButton()
    {
        var cancel = new Button { Content = Loc.Get("Cancel") };
        cancel.Click += (_, _) =>
        {
            _linker.Cancel();
            Form.Navigator.GoBack();
        };
        return cancel;
    }

    private string ErrorText(LinkFailure failure) => Loc.Get(failure switch
    {
        LinkFailure.Denied => "CodeErrDenied",
        LinkFailure.Expired => _entering ? "CodeErrExpiredInvite" : "CodeErrExpiredRequest",
        LinkFailure.Cancelled => "CodeErrCancelled",
        LinkFailure.NotFound => "LinkNotFound",
        LinkFailure.AlreadyClaimed => "LinkAlreadyClaimed",
        LinkFailure.WrongMode => "CodeErrWrongMode",
        LinkFailure.DeviceLimit => "AccountErrorDeviceLimit",
        LinkFailure.Throttled => "AccountErrorThrottled",
        LinkFailure.Network => "AccountErrorNetwork",
        _ => "AccountErrorUnknown",
    });

    /// <summary>Код или число крупно, по центру тонального блока на всю ширину; диктор читает по знакам.</summary>
    private static TextBlock Big(string text, double size, Windows.UI.Text.FontWeight weight, bool mono, string spoken)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        if (mono) block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        AutomationProperties.SetName(block, spoken);
        return block;
    }

    private static Border Tonal(UIElement child) => new()
    {
        Child = child,
        Width = Form.Width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(24, 20, 24, 20),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
    };

    private static HyperlinkButton Link(string text, Action action)
    {
        var button = new HyperlinkButton { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}
