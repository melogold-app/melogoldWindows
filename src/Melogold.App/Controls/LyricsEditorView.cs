using System.Globalization;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Lyrics;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Melogold.App.Controls;

/// <summary>
/// Редактор текста (<c>spec/lyrics.md</c>, «Редактор»; Android <c>LyricsEditorDialog</c>): вкладки «Текст»,
/// «Синхронизация», «Просмотр». Текст — строка песни на строку, подпевка в скобках в конце. Синхронизация — «Отметить»
/// (Enter) во время воспроизведения ставит начало строки или слова, «Конец строки» (Shift+Enter) — паузу после неё.
/// Сохранённый текст — свой: TTML и обычный рядом; через 2 с он уходит на сервер.
/// </summary>
public sealed partial class LyricsEditorView : Grid
{
    private const int MaxUndo = 200;

    /// <summary>Отметка раньше нажатия: человек нажимает чуть позже, чем слышит.</summary>
    private const long ReactionMs = 150;

    private const long RewindMs = 3_000;
    private const long NudgeMs = 100;

    /// <summary>Нажатие по отмеченной строке играет с чуть раньше неё.</summary>
    private const long ReplayLeadMs = 2_000;

    private readonly PlayerEngine _engine = App.Services.GetRequiredService<PlayerEngine>();
    private readonly LyricsService _lyrics = App.Services.GetRequiredService<LyricsService>();
    private readonly Track _track;
    private readonly StoredLyrics? _original;
    private readonly LyricsDraft _initial;
    private readonly List<LyricsDraft> _history = [];
    private readonly SelectorBar _tabs = new();
    private readonly Grid _body = new();
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        IsSpellCheckEnabled = false,
        Margin = new Thickness(24, 8, 24, 16),
    };
    private readonly Grid _sync = new();
    private readonly SelectorBar _timing = new() { Margin = new Thickness(16, 4, 16, 4) };
    private readonly ScrollViewer _linesScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _lines = new() { Spacing = 2, Margin = new Thickness(12, 0, 12, 8) };
    private readonly TextBlock _position = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
    private readonly TextBlock _next = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap };
    private readonly Button _mark = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
    private readonly Button _markEnd = new() { MinHeight = 48 };
    private readonly Button _undo = new();
    private readonly Grid _preview = new();
    private readonly SyncedLyricsView _previewLyrics = new();
    private readonly List<Button> _playButtons = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private LyricsDraft _draft;

    public LyricsEditorView(Track track, StoredLyrics? original, LyricsDraft initial)
    {
        _track = track;
        _original = original;
        _initial = _draft = initial;
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(this, Loc.Get("LyricsEditorTitle"));

        Children.Add(TopBar());

        foreach (var (key, tag) in new[] { ("LyricsEditorTabText", 0), ("LyricsEditorTabSync", 1), ("LyricsEditorTabPreview", 2) })
            _tabs.Items.Add(new SelectorBarItem { Text = Loc.Get(key), Tag = tag });
        _tabs.Margin = new Thickness(16, 0, 16, 0);
        _tabs.SelectionChanged += (_, _) => ShowTab();
        SetRow(_tabs, 1);
        Children.Add(_tabs);

        // Текст
        _text.Header = Loc.Get("LyricsEditorTextLabel");
        _text.Description = Loc.Get("LyricsEditorTextHint");
        _text.Text = _draft.ToText();
        _text.TextChanged += (_, _) => ShowUndo();
        AutomationProperties.SetName(_text, Loc.Get("LyricsEditorTextLabel"));

        BuildSync();
        BuildPreview();
        SetRow(_body, 2);
        Children.Add(_body);

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _clock = queue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(50);
        _clock.Tick += (_, _) => ShowTransport();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _previewLyrics.Stop();
        };
        // Enter — «Отметить», Shift+Enter — «Конец строки», раньше кнопки в фокусе (кроме полей ввода и перехода
        // клавишей Tab); пробел — пауза, как везде
        PreviewKeyDown += OnKeyDown;

        _tabs.SelectedItem = _tabs.Items[_draft.Lines.Count == 0 ? 0 : 1];
        ShowUndo();
    }

    /// <summary>Закрыт: сохранён или закрыт без сохранения.</summary>
    public event Action? Closed;

    private bool Changed => !_draft.Equals(_initial) || _text.Text != _draft.ToText();

    // ---------- Верх: ✕ · «Текст песни» · Отменить, Сохранить, ⋯ ----------

    private Grid TopBar()
    {
        var bar = new Grid { Padding = new Thickness(16, 8, 16, 4), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var close = IconButton("", Loc.Get("Cancel"), () => _ = CloseAsync());
        bar.Children.Add(close);

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = Loc.Get("LyricsEditorTitle"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        title.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(_track.ArtistsText) ? _track.Title : $"{_track.Title} · {_track.ArtistsText}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        SetColumn(title, 1);
        bar.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _undo.Content = new FontIcon { Glyph = "", FontSize = 16 };
        _undo.Click += (_, _) => Undo();
        AutomationProperties.SetName(_undo, Loc.Get("LyricsEditorUndo"));
        ToolTipService.SetToolTip(_undo, Loc.Get("LyricsEditorUndo") + " (Ctrl+Z)");
        _undo.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = this });
        actions.Children.Add(_undo);

        var save = new Button { Content = Loc.Get("LyricsEditorSave"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        save.Click += (_, _) => Save();
        save.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = this });
        ToolTipService.SetToolTip(save, "Ctrl+S");
        actions.Children.Add(save);

        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var language = new MenuFlyoutItem { Text = Loc.Get("LyricsEditorLanguage"), Icon = new FontIcon { Glyph = "" } };
        language.Click += async (_, _) =>
        {
            if (await AskAsync(Loc.Get("LyricsEditorLanguageTitle"), Loc.Get("LyricsEditorLanguageLabel"), _draft.Language) is { } code)
                Apply(d => d with { Language = code.Length == 0 ? null : code });
        };
        var ttml = new MenuFlyoutItem { Text = Loc.Get("LyricsEditorExportTtml"), Icon = new FontIcon { Glyph = "" } };
        ttml.Click += (_, _) => _ = ExportAsync(".ttml", TtmlFormat.Write);
        var lrc = new MenuFlyoutItem { Text = Loc.Get("LyricsEditorExportLrc"), Icon = new FontIcon { Glyph = "" } };
        lrc.Click += (_, _) => _ = ExportAsync(".lrc", l => LrcFormat.Write(l));
        menu.Items.Add(language);
        menu.Items.Add(ttml);
        menu.Items.Add(lrc);
        menu.Opening += (_, _) =>
        {
            CommitText();
            ttml.IsEnabled = lrc.IsEnabled = _draft.HasTiming;
        };
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 16 }, Flyout = menu };
        AutomationProperties.SetName(more, Loc.Get("MoreOptions"));
        actions.Children.Add(more);
        SetColumn(actions, 2);
        bar.Children.Add(actions);
        return bar;
    }

    private static Button IconButton(string glyph, string name, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Style = (Style)Application.Current.Resources["PlayerIconButtonStyle"] };
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    // ---------- Синхронизация ----------

    private void BuildSync()
    {
        _sync.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _sync.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sync.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Что отмечает нажатие: строки целиком или слово за словом
        _timing.Items.Add(new SelectorBarItem { Text = Loc.Get("LyricsEditorLines"), Tag = LyricsTiming.Line });
        _timing.Items.Add(new SelectorBarItem { Text = Loc.Get("LyricsEditorWords"), Tag = LyricsTiming.Word });
        _timing.SelectedItem = _timing.Items[_draft.Timing == LyricsTiming.Word ? 1 : 0];
        _timing.SelectionChanged += (_, _) =>
        {
            if (_timing.SelectedItem?.Tag is LyricsTiming timing && timing != _draft.Timing) Apply(d => (d with { Timing = timing }).MovedTo(d.Cursor));
        };
        _sync.Children.Add(_timing);

        _linesScroller.Content = _lines;
        SetRow(_linesScroller, 1);
        _sync.Children.Add(_linesScroller);

        // Низ: назад 3 с, play/pause, позиция и что отметит нажатие; «Конец строки» и большое «Отметить»
        var controls = new Grid
        {
            Padding = new Thickness(16),
            RowSpacing = 12,
            ColumnSpacing = 8,
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
        };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.Children.Add(Transport(_next));

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _markEnd.Content = Loc.Get("LyricsEditorEndLine");
        _markEnd.Click += (_, _) => MarkEnd();
        ToolTipService.SetToolTip(_markEnd, "Shift+Enter");
        buttons.Children.Add(_markEnd);
        _mark.Content = new TextBlock { Text = Loc.Get("LyricsEditorMark"), FontSize = 16, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetName(_mark, Loc.Get("LyricsEditorMark"));
        ToolTipService.SetToolTip(_mark, "Enter");
        _mark.Click += (_, _) => Mark();
        SetColumn(_mark, 1);
        buttons.Children.Add(_mark);
        SetRow(buttons, 1);
        controls.Children.Add(buttons);
        controls.Children.Add(new TextBlock
        {
            Text = Loc.Get("LyricsEditorKeysHint"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, 0, 0),
        });
        SetRow(controls, 2);
        _sync.Children.Add(controls);
    }

    /// <summary>Назад на 3 с, play/pause, позиция и подпись справа.</summary>
    private StackPanel Transport(TextBlock caption)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(IconButton("", Loc.Get("LyricsEditorRewind"), () => _engine.Seek(TimeSpan.FromMilliseconds(Math.Max(0, Position - RewindMs)))));
        var play = IconButton("", Loc.Get("Play"), () =>
        {
            if (_engine.IsPlaying) _engine.Pause();
            else _engine.Play();
        });
        _playButtons.Add(play);
        row.Children.Add(play);
        var position = caption == _next ? _position : new TextBlock { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        Typography.SetNumeralAlignment(position, FontNumeralAlignment.Tabular);
        row.Children.Add(position);
        if (caption != _next) _previewPosition = position;
        row.Children.Add(caption);
        return row;
    }

    private TextBlock? _previewPosition;

    private long Position => (long)_engine.Position.TotalMilliseconds;

    private void Mark()
    {
        if (_draft.Cursor >= _draft.Lines.Count) return;
        Apply(d => d.Mark(Math.Max(0, Position - ReactionMs)));
    }

    private void MarkEnd()
    {
        if (_draft.Cursor == 0) return;
        Apply(d => d.MarkEnd(Math.Max(0, Position - ReactionMs)));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || FocusManager.GetFocusedElement(XamlRoot) is TextBox or AutoSuggestBox or Control { FocusState: FocusState.Keyboard }) return;
        if (_tabs.SelectedItem?.Tag is not 1) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) MarkEnd();
        else Mark();
        e.Handled = true;
    }

    /// <summary>Строки: время, текст (в режиме слов отмеченные слова цветом, следующее — жирным с чертой), подпевка, сторона и меню.</summary>
    private void ShowLines()
    {
        _lines.Children.Clear();
        if (_draft.Lines.Count == 0)
        {
            _lines.Children.Add(Hint());
            return;
        }
        for (var index = 0; index < _draft.Lines.Count; index++) _lines.Children.Add(Row(index, _draft.Lines[index]));
        // Следующая строка — на виду: две строки над ней
        if (_draft.Cursor < _lines.Children.Count && _lines.Children[Math.Max(0, _draft.Cursor - 2)] is FrameworkElement target)
            DispatcherQueue.TryEnqueue(() => target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true }));
    }

    private TextBlock Hint() => new()
    {
        Text = Loc.Get("LyricsEditorNoLines"),
        Margin = new Thickness(12, 24, 12, 24),
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private Grid Row(int index, DraftLine line)
    {
        var isCursor = index == _draft.Cursor;
        var wordCursor = isCursor && _draft.Timing == LyricsTiming.Word ? _draft.WordCursor : -1;
        var row = new Grid
        {
            Padding = new Thickness(12, 8, 4, 8),
            ColumnSpacing = 12,
            CornerRadius = new CornerRadius(8),
            Background = isCursor ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new TextBlock
        {
            Text = line.StartMs is { } start ? FormatTime(start) : Loc.Get("LyricsEditorNoTime"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[line.StartMs is null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"],
        };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        row.Children.Add(time);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var main = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            TextAlignment = line.Side == VocalSide.End ? TextAlignment.Right : TextAlignment.Left,
        };
        if (wordCursor < 0 && line.WordStarts.Count == 0) main.Text = line.Text;
        else
        {
            var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            var words = line.Words;
            for (var i = 0; i < words.Count; i++)
            {
                var run = new Run { Text = words[i] };
                if (i < line.WordStarts.Count && line.WordStarts[i] is not null) run.Foreground = accent;
                if (i == wordCursor)
                {
                    run.FontWeight = FontWeights.Bold;
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                }
                main.Inlines.Add(run);
                if (i < words.Count - 1) main.Inlines.Add(new Run { Text = " " });
            }
        }
        texts.Children.Add(main);
        if (line.Backing is { } backing)
            texts.Children.Add(new TextBlock { Text = backing, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextAlignment = main.TextAlignment });
        SetColumn(texts, 1);
        row.Children.Add(texts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var sideName = $"{Loc.Get("LyricsEditorSide")}: {Loc.Get(line.Side == VocalSide.End ? "LyricsEditorSideEnd" : "LyricsEditorSideStart")}";
        buttons.Children.Add(IconButton(line.Side == VocalSide.End ? "" : "", sideName,
            () => Apply(d => d.WithSide(index, line.Side == VocalSide.Start ? VocalSide.End : VocalSide.Start))));
        var menu = new MenuFlyout();
        var timed = line.StartMs is not null;
        menu.Items.Add(MenuItem(Loc.Get("LyricsEditorEarlier"), timed, () => Apply(d => d.Nudge(index, -NudgeMs))));
        menu.Items.Add(MenuItem(Loc.Get("LyricsEditorLater"), timed, () => Apply(d => d.Nudge(index, NudgeMs))));
        menu.Items.Add(MenuItem(Loc.Get("LyricsEditorBacking"), true, async () =>
        {
            if (await AskAsync(Loc.Get("LyricsEditorBackingLabel"), Loc.Get("LyricsEditorBackingLabel"), line.Backing?.Trim('(', ')')) is { } text)
                Apply(d => d.WithBacking(index, text));
        }));
        menu.Items.Add(MenuItem(Loc.Get("LyricsEditorLineLanguage"), true, async () =>
        {
            if (await AskAsync(Loc.Get("LyricsEditorLanguageTitle"), Loc.Get("LyricsEditorLanguageLabel"), line.Language) is { } code)
                Apply(d => d.WithLineLanguage(index, code.Length == 0 ? null : code));
        }));
        menu.Items.Add(MenuItem(Loc.Get("LyricsEditorClearTime"), timed, () => Apply(d => d.ClearTiming(index).MovedTo(index))));
        var more = IconButton("", Loc.Get("MoreOptions"), () => { });
        more.Flyout = menu;
        buttons.Children.Add(more);
        SetColumn(buttons, 2);
        row.Children.Add(buttons);

        // Нажатие по строке: следующая отметка — она; у отмеченной — играть с чуть раньше
        row.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, buttons)) return;
            Apply(d => d.MovedTo(index));
            if (line.StartMs is { } at) _engine.Seek(TimeSpan.FromMilliseconds(Math.Max(0, at - ReplayLeadMs)));
        };
        row.ContextFlyout = menu;
        AutomationProperties.SetName(row, $"{(line.StartMs is { } s ? FormatTime(s) : Loc.Get("LyricsEditorNoTime"))} {line.FullText}");
        return row;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == container) return true;
        return false;
    }

    private static MenuFlyoutItem MenuItem(string text, bool enabled, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- Просмотр ----------

    private void BuildPreview()
    {
        _preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewLyrics.Position = () => Position;
        _previewLyrics.IsPlaying = () => _engine.IsPlaying;
        _previewLyrics.Seek = ms => _engine.Seek(TimeSpan.FromMilliseconds(ms));
        _previewLyrics.Margin = new Thickness(24, 0, 24, 0);
        _preview.Children.Add(_previewLyrics);
        var transport = Transport(new TextBlock());
        transport.HorizontalAlignment = HorizontalAlignment.Center;
        transport.Margin = new Thickness(16);
        SetRow(transport, 1);
        _preview.Children.Add(transport);
    }

    private void ShowPreview()
    {
        _previewLyrics.Stop();
        if (_draft.ToSyncedLyrics() is not { } lyrics)
        {
            _preview.Children[0].Visibility = Visibility.Collapsed;
            return;
        }
        _preview.Children[0].Visibility = Visibility.Visible;
        _previewLyrics.SetLyrics(LyricRows.Build(lyrics), 0);
        _previewLyrics.Start();
    }

    // ---------- Состояние ----------

    private void ShowTab()
    {
        CommitText();
        _body.Children.Clear();
        _previewLyrics.Stop();
        switch (_tabs.SelectedItem?.Tag)
        {
            case 0:
                _text.Text = _draft.ToText();
                _body.Children.Add(_text);
                break;
            case 1:
                ShowLines();
                _body.Children.Add(_sync);
                break;
            default:
                _body.Children.Add(_draft.HasTiming ? _preview : Hint());
                if (_draft.HasTiming) ShowPreview();
                break;
        }
    }

    /// <summary>Правка черновика: прежний — в «Отменить».</summary>
    private void Apply(Func<LyricsDraft, LyricsDraft> transform)
    {
        CommitText();
        var next = transform(_draft);
        if (next.Equals(_draft)) return;
        _history.Add(_draft);
        if (_history.Count > MaxUndo) _history.RemoveAt(0);
        _draft = next;
        Refresh();
    }

    /// <summary>Текст вкладки «Текст» — в черновик при уходе с неё (Отменить — не по буквам).</summary>
    private void CommitText()
    {
        if (_tabs.SelectedItem?.Tag is not 0 || _text.Text.Replace("\r", "\n") == _draft.ToText()) return;
        _history.Add(_draft);
        _draft = _draft.WithText(_text.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    private void Undo()
    {
        if (_tabs.SelectedItem?.Tag is 0 && _text.Text.Replace("\r", "\n") != _draft.ToText())
        {
            _text.Text = _draft.ToText();
            return;
        }
        if (_history.Count == 0) return;
        _draft = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Refresh();
    }

    private void Refresh()
    {
        if (_timing.SelectedItem?.Tag is LyricsTiming timing && timing != _draft.Timing)
            _timing.SelectedItem = _timing.Items[_draft.Timing == LyricsTiming.Word ? 1 : 0];
        switch (_tabs.SelectedItem?.Tag)
        {
            case 0:
                _text.Text = _draft.ToText();
                break;
            case 1:
                ShowLines();
                break;
            default:
                ShowTab();
                break;
        }
        ShowUndo();
    }

    private void ShowUndo() => _undo.IsEnabled = _history.Count > 0 || (_tabs.SelectedItem?.Tag is 0 && _text.Text.Replace("\r", "\n") != _draft.ToText());

    /// <summary>Позиция, play/pause и что отметит следующее нажатие.</summary>
    private void ShowTransport()
    {
        var text = FormatTime(Position);
        _position.Text = text;
        if (_previewPosition is not null) _previewPosition.Text = text;
        foreach (var button in _playButtons)
        {
            ((FontIcon)button.Content).Glyph = _engine.IsPlaying ? "" : "";
            AutomationProperties.SetName(button, Loc.Get(_engine.IsPlaying ? "Pause" : "Play"));
        }
        var line = _draft.Cursor < _draft.Lines.Count ? _draft.Lines[_draft.Cursor] : null;
        var next = line is null ? null
            : _draft.Timing == LyricsTiming.Word && line.Words.Count > 0 ? line.Words[Math.Min(_draft.WordCursor, line.Words.Count - 1)]
            : line.Text;
        _next.Text = next is null ? Loc.Get("LyricsEditorAllMarked") : Loc.Format("LyricsEditorNextFormat", next);
        _mark.IsEnabled = line is not null;
        _markEnd.IsEnabled = _draft.Cursor > 0;
    }

    private static string FormatTime(long ms) =>
        $"{ms / 60_000}:{ms / 1000 % 60:00}{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}{ms / 10 % 100:00}";

    // ---------- Сохранить, закрыть, экспорт ----------

    private void Save()
    {
        CommitText();
        _lyrics.SaveDraft(_track.VideoId, _original, _draft);
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("LyricsEditorSaved"));
        Closed?.Invoke();
    }

    /// <summary>Закрыть; с несохранёнными правками — сначала спросить.</summary>
    public async Task CloseAsync()
    {
        if (Changed)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("LyricsEditorDiscardTitle"),
                PrimaryButtonText = Loc.Get("LyricsEditorDiscard"),
                CloseButtonText = Loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        Closed?.Invoke();
    }

    private async Task ExportAsync(string extension, Func<SyncedLyrics, string> write)
    {
        CommitText();
        if (_draft.ToSyncedLyrics() is not { } lyrics || App.Current?.Window is not { } window) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary, SuggestedFileName = "lyrics" };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is not { } file) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, write(lyrics));
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("LyricsEditorExported"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Lyrics export failed", e);
            App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("BackupFailed"));
        }
    }

    /// <summary>Поле ввода в окне: язык или подпевка; null — отменено.</summary>
    private async Task<string?> AskAsync(string title, string label, string? value)
    {
        var box = new TextBox { Header = label, Text = value ?? "", IsSpellCheckEnabled = false, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = Loc.Get("Done"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
