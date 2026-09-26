using System.ComponentModel;
using System.Runtime.InteropServices;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App;

/// <summary>
/// Мини-плеер (docs/PROMPT.md §5.2): отдельное окно поверх остальных (<c>CompactOverlay</c>) — обложка, название,
/// ⏮ ⏯ ⏭ и «Развернуть». Пока он открыт, главное окно скрыто; закрытие мини-плеера возвращает главное.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private readonly PlayerViewModel _player = App.Services.GetRequiredService<PlayerViewModel>();
    private readonly Image _artwork = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _playGlyph = new() { FontSize = 16 };
    private readonly Button _play;

    public MiniPlayerWindow()
    {
        Title = "Melogold";
        SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;

        var root = new Grid { Padding = new Thickness(12), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(6), Child = _artwork, VerticalAlignment = VerticalAlignment.Center });

        var right = new Grid { RowSpacing = 4 };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Справа сверху — системная кнопка закрытия окна поверх содержимого
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 48, 0) };
        _subtitle.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        names.Children.Add(_title);
        names.Children.Add(_subtitle);
        right.Children.Add(names);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(Button("", "Previous", _player.PreviousCommand));
        _play = Button(null, "Play", _player.PlayPauseCommand);
        _play.Content = _playGlyph;
        buttons.Children.Add(_play);
        buttons.Children.Add(Button("", "Next", _player.NextCommand));
        Grid.SetRow(buttons, 1);
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        // «Развернуть» — в правом верхнем углу, под системной кнопкой закрытия её не видно
        var expand = Button("", "MiniPlayerExpand", null);
        expand.Click += (_, _) => Close();
        expand.HorizontalAlignment = HorizontalAlignment.Right;
        expand.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(expand, 1);
        root.Children.Add(expand);
        Content = root;
        SetTitleBar(names);
#if DEBUG
        Services.DebugSnapshot.Start(root, "shot-request-mini");
#endif

        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new Windows.Graphics.SizeInt32((int)(360 * scale), (int)(128 * scale));
        AppWindow.Resize(size);
        // Мини-плеер открывается там, где его оставили (если тот монитор на месте)
        var settings = App.Services.GetRequiredService<SettingsStore>();
        if (Melogold.Core.Domain.QuietMode.IsOn)
        {
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Move(new Windows.Graphics.PointInt32(WindowPlacement.OffScreenX(), 0));
        }
        else if (settings.MiniPlayerPosition?.Split(',') is [var sx, var sy]
            && int.TryParse(sx, System.Globalization.CultureInfo.InvariantCulture, out var x) && int.TryParse(sy, System.Globalization.CultureInfo.InvariantCulture, out var y)
            && WindowPlacement.OnScreen(x, y, size.Width, size.Height))
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange && AppWindow.IsVisible && !Melogold.Core.Domain.QuietMode.IsOn) settings.MiniPlayerPosition = FormattableString.Invariant($"{AppWindow.Position.X},{AppWindow.Position.Y}");
        };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "melogold.ico"));
        AppWindow.Title = "Melogold";

        _player.PropertyChanged += OnPlayerChanged;
        Closed += (_, _) => _player.PropertyChanged -= OnPlayerChanged;
        Update();
    }

    private static Button Button(string? glyph, string label, System.Windows.Input.ICommand? command)
    {
        var button = new Button { Style = (Style)Application.Current.Resources["PlayerIconButtonStyle"], Command = command };
        if (glyph is not null) button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        AutomationProperties.SetName(button, Loc.Get(label));
        ToolTipService.SetToolTip(button, Loc.Get(label));
        return button;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Update);

    private void Update()
    {
        _title.Text = _player.Title;
        _subtitle.Text = _player.Subtitle;
        _artwork.Source = Images.Player(_player.ArtworkUrl);
        _playGlyph.Glyph = _player.PlayPauseGlyph;
        AutomationProperties.SetName(_play, _player.PlayPauseLabel);
        ToolTipService.SetToolTip(_play, _player.PlayPauseLabel);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
