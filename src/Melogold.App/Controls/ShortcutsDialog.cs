using Melogold.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// «Сочетания клавиш» (F1 и Ctrl+/, «…» панели плеера, «Настройки → О приложении»): все сочетания клиента по
/// группам. Клавиши — плашками, как на клавиатуре. Запись сочетания: «+» соединяет клавиши, « / » — пара
/// «туда / обратно», « | » — «или».
/// </summary>
public static class ShortcutsDialog
{
    private static readonly (string Group, (string Keys, string Text)[] Items)[] Groups =
    [
        ("ShortcutsGroupPlayback",
        [
            ("Space", "ShortcutPlayPause"),
            ("Ctrl+→ / Ctrl+←", "ShortcutNextPrevious"),
            ("Shift+→ / Shift+←", "ShortcutSeek"),
            ("Ctrl+↑ / Ctrl+↓", "ShortcutVolume"),
            ("Ctrl+M | M", "ShortcutMute"),
            ("Ctrl+H", "ShortcutShuffle"),
            ("Ctrl+T", "ShortcutRepeat"),
            ("Ctrl+D", "ShortcutLike"),
        ]),
        ("ShortcutsGroupWindow",
        [
            ("Ctrl+F | /", "ShortcutSearch"),
            ("Ctrl+1 / Ctrl+2 / Ctrl+3", "ShortcutSections"),
            ("Ctrl+,", "ShortcutSettings"),
            ("Alt+← | Esc", "ShortcutBack"),
            ("Ctrl+L", "ShortcutLyrics"),
            ("Ctrl+Q", "ShortcutQueue"),
            ("Ctrl+Shift+M", "ShortcutMini"),
            ("F11", "ShortcutFullScreen"),
            ("F1 | Ctrl+/", "ShortcutHelp"),
        ]),
        ("ShortcutsGroupLists",
        [
            ("Enter", "ShortcutRowPlay"),
            ("Ctrl+A", "ShortcutSelectAll"),
            ("Esc", "ShortcutClearSelection"),
            ("Menu | Shift+F10", "ShortcutRowMenu"),
            ("Delete", "ShortcutRowRemove"),
            ("Alt+↑ / Alt+↓", "ShortcutRowMove"),
        ]),
        ("ShortcutsGroupEditor",
        [
            ("Enter", "ShortcutMark"),
            ("Shift+Enter", "ShortcutMarkEnd"),
            ("Backspace", "ShortcutRemark"),
            ("↑ / ↓", "ShortcutCursor"),
            ("← / →", "ShortcutEditorSeek"),
            ("[ / ]", "ShortcutNudge"),
            ("Ctrl+Z", "ShortcutUndo"),
            ("Ctrl+S", "ShortcutSave"),
        ]),
    ];

    private static bool _open;

    public static async Task ShowAsync(XamlRoot root)
    {
        // Второе F1 при открытом окне — ничего: ContentDialog один на окно
        if (_open) return;
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Loc.Get("ShortcutsTitle"),
                CloseButtonText = Loc.Get("Close"),
                DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer { Content = Body(), MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) },
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720.0;
            await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }

    private static StackPanel Body()
    {
        var body = new StackPanel { Spacing = 20, MinWidth = 520 };
        foreach (var (group, items) in Groups)
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock { Text = Loc.Get(group), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 2) });
            foreach (var (keys, text) in items) section.Children.Add(Row(keys, Loc.Get(text)));
            body.Children.Add(section);
        }
        return body;
    }

    private static Grid Row(string keys, string text)
    {
        var row = new Grid { ColumnSpacing = 16, MinHeight = 32 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var combo = Keys(keys);
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        AutomationProperties.SetName(row, $"{text}: {Spoken(keys)}");
        return row;
    }

    /// <summary>Сочетание плашками: «Ctrl» «→» / «Ctrl» «←», «F1» или «Ctrl» «/».</summary>
    private static StackPanel Keys(string keys)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var alternatives = keys.Split(" | ");
        for (var a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) panel.Children.Add(Separator(Loc.Get("ShortcutOr")));
            var pair = alternatives[a].Split(" / ");
            for (var p = 0; p < pair.Length; p++)
            {
                if (p > 0) panel.Children.Add(Separator("/"));
                foreach (var key in Split(pair[p])) panel.Children.Add(Key(key));
            }
        }
        return panel;
    }

    /// <summary>Клавиши сочетания; «Ctrl+,» и «Ctrl+/» — две клавиши, «+» внутри не бывает.</summary>
    private static IEnumerable<string> Split(string combo) => combo.Split('+', StringSplitOptions.RemoveEmptyEntries);

    private static Border Key(string key) => new()
    {
        MinWidth = 28,
        Padding = new Thickness(8, 3, 8, 3),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1, 1, 1, 2),
        Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        Child = new TextBlock
        {
            Text = Label(key),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold, Segoe UI"),
        },
    };

    private static TextBlock Separator(string text) => new()
    {
        Text = text,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static string Label(string key) => key switch
    {
        "Space" => Loc.Get("KeySpace"),
        "Menu" => Loc.Get("KeyMenu"),
        _ => key,
    };

    /// <summary>Для экранного диктора: «Ctrl плюс стрелка вправо» читается и так, стрелки — символами.</summary>
    private static string Spoken(string keys) => string.Join($" {Loc.Get("ShortcutOr")} ", keys.Split(" | ").Select(a => string.Join(" / ", a.Split(" / ").Select(p => string.Join(" + ", Split(p).Select(Label))))));
}
