using Melogold.App.Services;
using Melogold.InnerTube;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Четыре состояния экрана (§5.5): загрузка — <see cref="ProgressRing"/> по центру видимой области, появляется через
/// 300 мс, без скелетонов; контент; пусто — иконка, заголовок, строка пояснения и кнопки; ошибка — тексты REWRITE §3.0
/// и «Повторить».
/// </summary>
public sealed partial class StateView : Grid
{
    private readonly ProgressRing _ring = new() { Width = 40, Height = 40, IsActive = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _message = new() { Spacing = 12, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly InfoBar _stale = new() { Severity = InfoBarSeverity.Informational, IsClosable = true, Margin = new Thickness(0, 0, 0, 12) };
    private CancellationTokenSource? _delay;

    public StateView()
    {
        MinHeight = 240;
        Children.Add(_ring);
        Children.Add(_message);
        Children.Add(_stale);
        _stale.Closed += (_, _) => Visibility = Visibility.Collapsed;
    }

    /// <summary>Контент из кэша без сети: «Нет сети — данные от 14:02» над ним (§5.5).</summary>
    public void ShowStale(DateTime at)
    {
        _delay?.Cancel();
        _ring.IsActive = false;
        _message.Visibility = Visibility.Collapsed;
        MinHeight = 0;
        _stale.Message = Loc.Format("OfflineDataFromFormat", at.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        _stale.IsOpen = true;
        Visibility = Visibility.Visible;
    }

    /// <summary>Идёт загрузка: кольцо — через 300 мс, чтобы быстрый ответ не мигал.</summary>
    public async void ShowLoading()
    {
        _stale.IsOpen = false;
        MinHeight = 240;
        Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Collapsed;
        _delay?.Cancel();
        var delay = _delay = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, delay.Token);
            _ring.IsActive = true;
        }
        catch (TaskCanceledException)
        {
        }
    }

    public void ShowContent()
    {
        _stale.IsOpen = false;
        _delay?.Cancel();
        _ring.IsActive = false;
        Visibility = Visibility.Collapsed;
    }

    public void ShowError(Exception error, Action retry)
    {
        var key = error switch
        {
            YouTubeException { Kind: YouTubeErrorKind.Offline } => "ErrorOffline",
            YouTubeException { Kind: YouTubeErrorKind.Blocked } => "ErrorBlocked",
            YouTubeException { Kind: YouTubeErrorKind.Parser } => "ErrorParser",
            HttpRequestException => "ErrorOffline",
            _ => "ErrorUnknown",
        };
        Log.Warn("Screen error", error);
        ShowMessage("", Loc.Get(key), null, (Loc.Get("Retry"), retry));
    }

    public void ShowEmpty(string glyph, string title, string? text = null, params (string Label, Action Action)[] actions) =>
        ShowMessage(glyph, title, text, actions);

    private void ShowMessage(string glyph, string title, string? text, params (string Label, Action Action)[] actions)
    {
        _stale.IsOpen = false;
        MinHeight = 240;
        _delay?.Cancel();
        _ring.IsActive = false;
        Visibility = Visibility.Visible;
        _message.Children.Clear();
        _message.Children.Add(new FontIcon { Glyph = glyph, FontSize = 40, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        _message.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
            HorizontalTextAlignment = TextAlignment.Center,
        });
        if (!string.IsNullOrEmpty(text))
        {
            _message.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.WrapWholeWords,
                HorizontalTextAlignment = TextAlignment.Center,
            });
        }
        if (actions.Length > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            for (var i = 0; i < actions.Length; i++)
            {
                var (label, action) = actions[i];
                var button = new Button { Content = label };
                if (i == 0) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                button.Click += (_, _) => action();
                buttons.Children.Add(button);
            }
            _message.Children.Add(buttons);
        }
        _message.Visibility = Visibility.Visible;
    }
}
