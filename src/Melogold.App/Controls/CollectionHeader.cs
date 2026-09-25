using Melogold.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melogold.App.Controls;

/// <summary>
/// Шапка детального экрана (§5.4): обложка около 200 px (у исполнителя — круг), название, подзаголовок и кнопки
/// «Слушать · Перемешать · Сохранить · …». Описание — три строки, «Ещё» раскрывает целиком. Уже 560 — обложка 160
/// сверху, текст и кнопки под ней во всю ширину. Без картинки («Все треки», пустой плейлист) — нота, а не пустой квадрат.
/// </summary>
public sealed partial class CollectionHeader : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _artwork;
    private readonly FontIcon _placeholder = new() { Glyph = "\uE8D6", FontSize = 56, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], Visibility = Visibility.Collapsed };
    private readonly StackPanel _text = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
    private const double NarrowWidth = 560;
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Style = (Style)Application.Current.Resources["BodyTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.WrapWholeWords };
    private readonly TextBlock _details = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Visibility = Visibility.Collapsed };
    private readonly HyperlinkButton _more = new() { Padding = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };

    public CollectionHeader()
    {
        ColumnSpacing = 24;
        Margin = new Thickness(0, 0, 0, 16);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var artworkContent = new Grid();
        artworkContent.Children.Add(_placeholder);
        artworkContent.Children.Add(_image);
        _artwork = new Border
        {
            Width = 200,
            Height = 200,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = artworkContent,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Children.Add(_artwork);
        _more.Content = Loc.Get("ResultsMore");
        _more.Click += (_, _) =>
        {
            _description.MaxLines = 0;
            _more.Visibility = Visibility.Collapsed;
        };
        _text.Children.Add(_title);
        _text.Children.Add(_subtitle);
        _text.Children.Add(_details);
        _text.Children.Add(_description);
        _text.Children.Add(_more);
        _text.Children.Add(_buttons);
        SetColumn(_text, 1);
        Children.Add(_text);
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(_title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
    }

    /// <summary>Узко — обложка сверху и меньше, текст под ней; широко — рядом.</summary>
    private void Fit(double width)
    {
        var narrow = width < NarrowWidth;
        _artwork.Width = _artwork.Height = narrow ? 160 : 200;
        RowSpacing = narrow ? 16 : 0;
        SetRow(_text, narrow ? 1 : 0);
        SetColumn(_text, narrow ? 0 : 1);
        SetColumnSpan(_text, narrow ? 2 : 1);
    }

    public void Set(string title, string? subtitle, string? details, string? imageUrl, bool round = false, string? description = null)
    {
        _title.Text = title;
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _details.Text = details ?? "";
        _details.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        _artwork.CornerRadius = round ? new CornerRadius(100) : new CornerRadius(8);
        _image.Source = Images.From(imageUrl, 400);
        _placeholder.Visibility = string.IsNullOrEmpty(imageUrl) ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(description))
        {
            _description.Text = description.Trim();
            _description.Visibility = Visibility.Visible;
            _more.Visibility = description.Length > 240 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public Button AddButton(string label, string glyph, Action action, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        _buttons.Children.Add(button);
        return button;
    }

    /// <summary>Переключатель «Сохранить / ✓ В библиотеке» (или «Подписаться / Вы подписаны»).</summary>
    public ToggleButton AddToggle(Func<bool, string> label, bool isOn, Action<bool> changed)
    {
        var icon = new FontIcon { FontSize = 14 };
        var text = new TextBlock();
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(icon);
        content.Children.Add(text);
        var toggle = new ToggleButton { Content = content, IsChecked = isOn };
        void Update()
        {
            var on = toggle.IsChecked == true;
            icon.Glyph = on ? "" : "";
            text.Text = label(on);
        }
        Update();
        toggle.Click += (_, _) =>
        {
            Update();
            changed(toggle.IsChecked == true);
        };
        _buttons.Children.Add(toggle);
        return toggle;
    }

    public Button AddMenu(MenuFlyout menu)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = menu,
            Padding = new Thickness(10, 8, 10, 8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Loc.Get("RowMenu"));
        _buttons.Children.Add(button);
        return button;
    }
}
