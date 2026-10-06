using Melogold.App.Services;
using Melogold.Core.Music;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Melogold.App.Controls;

/// <summary>
/// Шапка исполнителя, как в Apple Music (tasks/0024, исключение Melogold в docs/design/EXCEPTIONS.md): фото во всю ширину
/// от верхнего края страницы, внизу затемнение, поверх — имя крупно и круглые кнопки. Затемнение одинаковое в светлой и
/// тёмной теме, поэтому имя и кнопки всегда в тёмной теме — светлые на тёмном. Высота — по пропорциям фото, но не меньше
/// 280 и не больше 60 % окна; длинное имя переносится, а шапка растёт, а не обрезает его «…».
/// </summary>
public sealed partial class ArtistHero : Grid
{
    private const double MinPhotoHeight = 280;
    private const double NarrowWidth = 600;
    private readonly ImageBrush _photo = new() { Stretch = Stretch.UniformToFill };
    private readonly StackPanel _overlay = new() { Spacing = 16, VerticalAlignment = VerticalAlignment.Bottom, RequestedTheme = ElementTheme.Dark };
    private readonly TextBlock _name = new() { TextWrapping = TextWrapping.WrapWholeWords };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private double _aspect = 2.4;

    public ArtistHero()
    {
        // До загрузки фото — тёмная подложка: светлый текст читается сразу
        Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(255, 0x2B, 0x2B, 0x2B)) });
        Children.Add(new Border { Background = _photo });
        var shade = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.35 });
        shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xB8, 0, 0, 0), Offset = 1 });
        Children.Add(new Rectangle { Fill = shade });
        AutomationProperties.SetHeadingLevel(_name, AutomationHeadingLevel.Level1);
        _overlay.Children.Add(_name);
        _overlay.Children.Add(_buttons);
        Children.Add(_overlay);
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
    }

    /// <summary>Имя и фото. Широкое фото YTM (2,4 : 1) — по центру, квадратное — по верхнему краю, где обычно лицо.</summary>
    public void Set(string name, string? imageUrl, double scale)
    {
        _name.Text = name;
        _aspect = Thumbnails.Aspect(imageUrl) ?? 1;
        _photo.AlignmentY = _aspect >= 1.5 ? AlignmentY.Center : AlignmentY.Top;
        // Фото во всю ширину окна: запрашиваем ширину по экрану, кратную 360, — меньше разных адресов в кэше картинок
        var width = (int)Math.Clamp(Math.Ceiling(Math.Max(ActualWidth, 1200) * scale / 360) * 360, 720, 2880);
        _photo.ImageSource = Images.From(_aspect >= 1.5 ? Thumbnails.Wide(imageUrl, width) : Thumbnails.Sized(imageUrl, Math.Min(width, 1440)));
        Fit(ActualWidth);
    }

    /// <summary>Поля по бокам — как у содержимого страницы.</summary>
    public double Side { get; private set; } = 36;

    private void Fit(double width)
    {
        if (width <= 0) return;
        var narrow = width < NarrowWidth;
        Side = narrow ? 16 : 36;
        _overlay.Margin = new Thickness(Side, 0, Side, narrow ? 20 : 28);
        _name.Style = (Style)Application.Current.Resources[narrow ? "TitleTextBlockStyle" : "TitleLargeTextBlockStyle"];
        var windowHeight = XamlRoot?.Size.Height is > 0 and var h ? h : 800;
        // MinHeight, а не Height: длинному имени на крупном шрифте шапка уступает место
        MinHeight = Math.Clamp(width / _aspect, MinPhotoHeight, Math.Max(MinPhotoHeight, windowHeight * 0.6));
    }

    /// <summary>Кнопки заново — при повторной загрузке.</summary>
    public void ClearButtons() => _buttons.Children.Clear();

    /// <summary>Круглая кнопка со значком; подпись — подсказкой и для диктора.</summary>
    public Button AddButton(string glyph, string label, Action action, bool accent = false)
    {
        var button = Round(new Button(), glyph, label, accent ? 56 : 44);
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        _buttons.Children.Add(button);
        return button;
    }

    /// <summary>Круглый переключатель: «Подписаться» ⊕ / «Вы подписаны» ✓.</summary>
    public ToggleButton AddToggle(Func<bool, string> label, bool isOn, Action<bool> changed)
    {
        var toggle = Round(new ToggleButton { IsChecked = isOn }, "", "", 44);
        void Update()
        {
            var on = toggle.IsChecked == true;
            ((FontIcon)toggle.Content).Glyph = on ? "" : "";
            ToolTipService.SetToolTip(toggle, label(on));
            AutomationProperties.SetName(toggle, label(on));
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

    /// <summary>«…» — меню коллекции.</summary>
    public Button AddMenu(MenuFlyout menu)
    {
        var button = Round(new Button { Flyout = menu }, "", Loc.Get("MoreOptions"), 44);
        _buttons.Children.Add(button);
        return button;
    }

    private static T Round<T>(T button, string glyph, string label, double size) where T : ButtonBase
    {
        button.Width = button.Height = size;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(size / 2);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Content = new FontIcon { Glyph = glyph, FontSize = size >= 56 ? 22 : 16 };
        if (label.Length > 0)
        {
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, label);
        }
        return button;
    }
}
