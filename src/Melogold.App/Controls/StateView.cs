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
    private CancellationTokenSource? _delay;

    public StateView()
    {
        MinHeight = 240;
        Children.Add(_ring);
        Children.Add(_message);
    }

    /// <summary>Идёт загрузка: кольцо — через 300 мс, чтобы быстрый ответ не мигал.</summary>
    public async void ShowLoading()
    {
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
