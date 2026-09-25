using System.Numerics;
using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Melogold.App.Controls;

/// <summary>
/// «Сейчас играет» (docs/PROMPT.md §5.2): страница поверх окна, Esc закрывает её. Фон — мягкий оттенок по цвету
/// обложки (Material Color Utilities, как на Android), размытой обложки нет. В широком окне обложка слева, текст
/// справа; в узком — переключатель «Обложка · Текст». Управление остаётся в панели воспроизведения внизу.
/// </summary>
public sealed partial class NowPlayingView : Grid
{
    private const double WideWidth = 900;

    private readonly PlayerEngine _engine = App.Services.GetRequiredService<PlayerEngine>();
    private readonly LyricsService _lyrics = App.Services.GetRequiredService<LyricsService>();
    private readonly SolidColorBrush _background = new(Colors.Transparent);
    private readonly Grid _body = new() { ColumnSpacing = 64 };
    private readonly Grid _artPanel = new() { RowSpacing = 16, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
    private readonly Grid _lyricsPanel = new();
    private readonly Border _artwork = new() { CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Image _artworkImage = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SelectorBar _mode = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly SyncedLyricsView _synced = new();
    private readonly ScrollViewer _plainScroller = new() { Padding = new Thickness(16, 0, 16, 0) };
    private readonly TextBlock _plain = new() { FontSize = 22, LineHeight = 34, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, 24, 0, 48) };
    private readonly StackPanel _message = new() { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _source = new() { FontSize = 12, Margin = new Thickness(16, 8, 16, 0) };
    private readonly Button _close = new();
    private readonly List<TextBlock> _secondaryTexts = [];
    private ArtworkPalette? _palette;
    private string? _paletteKey;
    private bool _showLyricsInNarrow = true;
    private CancellationTokenSource? _paletteLoad;

    public NowPlayingView()
    {
        Visibility = Visibility.Collapsed;
        Background = _background;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Верх: «Свернуть» и переключатель в узком окне. Меню текста — в «…» панели плеера, одно на всё
        var top = new Grid { Padding = new Thickness(16, 8, 16, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _close.Content = new FontIcon { Glyph = "", FontSize = 16 };
        _close.Style = (Style)Application.Current.Resources["PlayerIconButtonStyle"];
        AutomationProperties.SetName(_close, Loc.Get("CollapsePlayer"));
        ToolTipService.SetToolTip(_close, Loc.Get("CollapsePlayer"));
        _close.Click += (_, _) => Close();
        top.Children.Add(_close);
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("NowPlayingArtwork"), Tag = "art" });
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("PlayerLyrics"), Tag = "lyrics" });
        _mode.SelectionChanged += (_, _) =>
        {
            _showLyricsInNarrow = (string?)_mode.SelectedItem?.Tag == "lyrics";
            Arrange();
        };
        // Посередине окна, а не колонки между кнопками
        SetColumnSpan(_mode, 3);
        top.Children.Add(_mode);
        Children.Add(top);

        // Обложка, название, исполнитель
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artwork.Child = _artworkImage;
        _artPanel.Children.Add(_artwork);
        // Название ведёт к альбому, исполнитель — к исполнителю, как в панели плеера
        var names = new StackPanel { Spacing = 4 };
        names.Children.Add(Link(_title, _ => { if (_engine.Current is { } track) App.Services.GetRequiredService<TrackActions>().OpenAlbumOrArtist(track); }));
        names.Children.Add(Link(_artist, anchor => { if (_engine.Current is { } track) App.Services.GetRequiredService<TrackActions>().OpenArtist(track, anchor); }));
        _secondaryTexts.Add(_artist);
        SetRow(names, 1);
        _artPanel.Children.Add(names);

        // Текст: синхронный, обычный или сообщение
        _lyricsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _lyricsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _plainScroller.Content = _plain;
        // Обычный текст растёт с окном так же, как синхронный
        _lyricsPanel.SizeChanged += (_, e) =>
        {
            var size = SyncedLyricsView.FontSizeFor(e.NewSize.Width, e.NewSize.Height);
            _plain.FontSize = size * 22 / 28;
            _plain.LineHeight = size * 34 / 28;
        };
        _lyricsPanel.Children.Add(_synced);
        _lyricsPanel.Children.Add(_plainScroller);
        _lyricsPanel.Children.Add(_message);
        SetRow(_source, 1);
        _lyricsPanel.Children.Add(_source);
        _secondaryTexts.Add(_source);
        _synced.Position = () => (long)_engine.Position.TotalMilliseconds;
        _synced.IsPlaying = () => _engine.IsPlaying;
        _synced.Seek = ms =>
        {
            _engine.Seek(TimeSpan.FromMilliseconds(ms));
            if (!_engine.IsPlaying) _engine.Play();
        };

        _body.Padding = new Thickness(48, 16, 48, 24);
        _body.Children.Add(_artPanel);
        _body.Children.Add(_lyricsPanel);
        SetRow(_body, 1);
        Children.Add(_body);

        SizeChanged += (_, _) => Arrange();
        _engine.TrackChanged += OnTrackChanged;
        _lyrics.Changed += ShowLyrics;
        ActualThemeChanged += (_, _) => _ = LoadPaletteAsync(force: true);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public event Action<bool>? OpenChanged;

    private static bool AnimationsEnabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    /// <summary>
    /// Открыть; <paramref name="lyrics"/> — в узком окне сразу на тексте; <paramref name="from"/> — обложка в панели
    /// плеера: она переезжает на место большой (<c>ConnectedAnimation</c>, §5.5).
    /// </summary>
    public void Open(bool lyrics = false, UIElement? from = null)
    {
        if (_engine.Current is null) return;
        ConnectedAnimation? flight = null;
        if (!IsOpen && from is not null && AnimationsEnabled)
            flight = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("NowPlayingArtwork", from);
        if (lyrics) _showLyricsInNarrow = true;
        _mode.SelectedItem = _mode.Items[_showLyricsInNarrow ? 1 : 0];
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            Animate(opening: true);
            OpenChanged?.Invoke(true);
        }
        _lyrics.Active = true;
        ShowTrack();
        ShowLyrics();
        _synced.Start();
        _close.Focus(FocusState.Programmatic);
        if (flight is not null)
        {
            // После раскладки: большая обложка должна знать своё место
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_artPanel.Visibility != Visibility.Visible || !flight.TryStart(_artwork)) flight.Cancel();
            });
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        // Открыт редактор текста: сначала он — с вопросом, если есть несохранённое
        if (_editor is not null)
        {
            _ = _editor.CloseAsync();
            return;
        }
        _lyrics.Active = false;
        _synced.Stop();
        Animate(opening: false);
        OpenChanged?.Invoke(false);
    }

    public void Toggle(bool lyrics = false, UIElement? from = null)
    {
        if (IsOpen) Close();
        else Open(lyrics, from);
    }

    private void Animate(bool opening)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        if (!AnimationsEnabled)
        {
            if (!opening) Visibility = Visibility.Collapsed;
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(this, true);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, opening ? 48 : 0, 0));
        move.InsertKeyFrame(1, new Vector3(0, opening ? 0 : 48, 0), easing);
        move.Duration = TimeSpan.FromMilliseconds(opening ? 300 : 180);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, opening ? 0 : 1);
        fade.InsertKeyFrame(1, opening ? 1 : 0, easing);
        fade.Duration = move.Duration;
        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        if (!opening) batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_lyrics.Active) Visibility = Visibility.Collapsed;
        });
    }

    private void OnTrackChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsOpen) return;
        if (_engine.Current is null)
        {
            Close();
            return;
        }
        ShowTrack();
    });

    private void ShowTrack()
    {
        if (_engine.Current is not { } track) return;
        _title.Text = track.Title;
        _artist.Text = track.ArtistsText ?? "";
        _artworkImage.Source = Images.From(Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 720));
        AutomationProperties.SetName(this, $"{Loc.Get("NowPlaying")}: {track.Title}");
        _ = LoadPaletteAsync(force: false);
    }

    /// <summary>Фон и цвета текста по обложке; у серой обложки — цвета темы.</summary>
    private async Task LoadPaletteAsync(bool force)
    {
        if (_engine.Current is not { } track) return;
        if (!force && _paletteKey == track.VideoId) return;
        _paletteKey = track.VideoId;
        _paletteLoad?.Cancel();
        var cancel = _paletteLoad = new CancellationTokenSource();
        var dark = ActualTheme == ElementTheme.Dark;
        var url = Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 226);
        // Высокая контрастность: цвета системы, без оттенка обложки
        var palette = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            ? new ArtworkPalette(SystemColor("SystemColorWindowColor"), SystemColor("SystemColorWindowTextColor"), SystemColor("SystemColorWindowTextColor"), SystemColor("SystemColorHighlightColor"))
            : await ArtworkColors.PaletteAsync(track.VideoId, url, dark, cancel.Token);
        if (cancel.IsCancellationRequested) return;
        _palette = palette ?? (dark
            ? new ArtworkPalette(Color.FromArgb(255, 0x20, 0x20, 0x20), Colors.White, Color.FromArgb(255, 0xC5, 0xC5, 0xC5), Color.FromArgb(255, 0x3A, 0x3A, 0x3A))
            : new ArtworkPalette(Color.FromArgb(255, 0xF3, 0xF3, 0xF3), Color.FromArgb(255, 0x1A, 0x1A, 0x1A), Color.FromArgb(255, 0x5C, 0x5C, 0x5C), Color.FromArgb(255, 0xE0, 0xE0, 0xE0)));
        _background.Color = _palette.Background;
        _title.Foreground = new SolidColorBrush(_palette.Text);
        _plain.Foreground = new SolidColorBrush(_palette.Text);
        foreach (var text in _secondaryTexts) text.Foreground = new SolidColorBrush(_palette.SecondaryText);
        foreach (var text in _message.Children.OfType<TextBlock>()) text.Foreground = new SolidColorBrush(_palette.Text);
        _synced.SetColors(_palette.Text, _palette.Pill, _palette.Background);
    }

    /// <summary>Текст, который нажимается: без подчёркивания и цвета ссылки, цвет — от обложки.</summary>
    private static HyperlinkButton Link(TextBlock text, Action<FrameworkElement> open)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += (_, _) => open(link);
        return link;
    }

    private static Color SystemColor(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Color color ? color : Colors.Black;

    /// <summary>Широкое окно — обложка и текст рядом; узкое — одно из двух по переключателю.</summary>
    private void Arrange()
    {
        var wide = ActualWidth >= WideWidth;
        _mode.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _body.ColumnDefinitions.Clear();
        var available = ActualWidth - 96;
        double side;
        if (wide)
        {
            // Две равные колонки по обе стороны от середины окна (там же Play в панели плеера): обложка прижата к середине
            // слева, текст начинается сразу справа от неё; колонка не шире 760, обложка до 480 и не выше окна
            var half = Math.Min(760, (available - _body.ColumnSpacing) / 2);
            side = Math.Max(120, Math.Min(Math.Min(480, ActualHeight - 220), half));
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(half) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(half) });
            _body.HorizontalAlignment = HorizontalAlignment.Center;
            _artPanel.HorizontalAlignment = HorizontalAlignment.Right;
            // Ширина — ровно обложка: длинное название переносится под ней и не отодвигает её от середины
            _artPanel.Width = side;
            SetColumn(_lyricsPanel, 1);
            _artPanel.Visibility = Visibility.Visible;
            _lyricsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            side = Math.Max(120, Math.Min(Math.Min(available, 480), ActualHeight - 220));
            _body.HorizontalAlignment = HorizontalAlignment.Stretch;
            _artPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _artPanel.Width = double.NaN;
            SetColumn(_lyricsPanel, 0);
            _artPanel.Visibility = _showLyricsInNarrow ? Visibility.Collapsed : Visibility.Visible;
            _lyricsPanel.Visibility = _showLyricsInNarrow ? Visibility.Visible : Visibility.Collapsed;
        }
        _artwork.Width = _artwork.Height = side;
    }

    private void ShowLyrics()
    {
        var state = _lyrics.State;
        _synced.Visibility = state is LyricsState.Synced ? Visibility.Visible : Visibility.Collapsed;
        _plainScroller.Visibility = state is LyricsState.Plain ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = state is LyricsState.Synced or LyricsState.Plain ? Visibility.Collapsed : Visibility.Visible;
        _message.Children.Clear();
        string? source = null;
        switch (state)
        {
            case LyricsState.Synced synced:
                _synced.SetLyrics(synced.Rows, synced.OffsetMs);
                source = synced.Source;
                break;
            case LyricsState.Plain plain:
                _plain.Text = plain.Text;
                _plainScroller.ChangeView(null, 0, null, true);
                source = plain.Source;
                break;
            case LyricsState.Loading:
                _message.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
                _message.Children.Add(Message(Loc.Get("LyricsLoading")));
                break;
            case LyricsState.NotFound:
                _message.Children.Add(Message(Loc.Get("LyricsUnavailable")));
                _message.Children.Add(MessageButton(Loc.Get("LyricsFind"), () => _ = FindAsync()));
                _message.Children.Add(MessageButton(Loc.Get("LyricsEdit"), OpenEditor));
                break;
            case LyricsState.Failed:
                _message.Children.Add(Message(Loc.Get("LyricsLoadFailed")));
                _message.Children.Add(MessageButton(Loc.Get("Retry"), _lyrics.Retry));
                break;
        }
        _source.Text = source switch
        {
            LyricsSources.YouTubeMusic => Loc.Get("LyricsSourceYouTubeMusic"),
            LyricsSources.LrcLib => Loc.Get("LyricsSourceLrcLib"),
            LyricsSources.KuGou => Loc.Get("LyricsSourceKuGou"),
            LyricsSources.File => Loc.Get("LyricsSourceFile"),
            LyricsSources.User => Loc.Get("LyricsSourceUser"),
            LyricsSources.Melogold => Loc.Get("LyricsSourceMelogold"),
            _ => "",
        };
    }

    private TextBlock Message(string text) => new()
    {
        Text = text,
        FontSize = 18,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(_palette?.Text ?? Colors.Gray),
    };

    private static Button MessageButton(string text, Action action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Группа «Текст» меню «…» панели плеера (§5.2), пока текст на экране: переключатель подписан по тому, что на
    /// экране; «Найти другой текст» — LRCLIB и файл; «Редактировать текст»; «Сдвиг текста» на ±0,1 и ±0,5 с.
    /// Как на Android (REWRITE §3.10.5): одно меню у плеера, отдельного меню текста нет.
    /// </summary>
    public void AddLyricsItems(IList<MenuFlyoutItemBase> items)
    {
        if (!IsOpen || _editor is not null || _lyricsPanel.Visibility != Visibility.Visible || _lyrics.Track is null) return;
        items.Add(new MenuFlyoutSeparator());
        var state = _lyrics.State;
        if (state is LyricsState.Synced)
            items.Add(Item(Loc.Get("LyricsShowPlain"), "\uED1E", _lyrics.ToggleSynced));
        else if (state is LyricsState.Plain && _lyrics.HasSynced)
            items.Add(Item(Loc.Get("LyricsShowSynced"), "\uED1E", _lyrics.ToggleSynced));
        items.Add(Item(Loc.Get("LyricsFindMenu"), "\uE721", () => _ = FindAsync()));
        items.Add(Item(Loc.Get("LyricsEdit"), "\uE70F", OpenEditor));
        if (state is LyricsState.Synced synced)
        {
            var shift = (synced.OffsetMs / 1000.0).ToString("+0.0;-0.0", System.Globalization.CultureInfo.CurrentCulture);
            var offset = new MenuFlyoutSubItem
            {
                Text = synced.OffsetMs == 0 ? Loc.Get("LyricsOffset") : Loc.Format("LyricsOffsetFormat", shift),
                Icon = new FontIcon { Glyph = "\uE916" },
            };
            offset.Items.Add(Item(Loc.Get("LyricsEarlier05"), null, () => _lyrics.Shift(500)));
            offset.Items.Add(Item(Loc.Get("LyricsEarlier01"), null, () => _lyrics.Shift(100)));
            offset.Items.Add(Item(Loc.Get("LyricsLater01"), null, () => _lyrics.Shift(-100)));
            offset.Items.Add(Item(Loc.Get("LyricsLater05"), null, () => _lyrics.Shift(-500)));
            if (synced.OffsetMs != 0)
            {
                offset.Items.Add(new MenuFlyoutSeparator());
                offset.Items.Add(Item(Loc.Format("LyricsResetShiftFormat", shift), null, _lyrics.ResetShift));
            }
            items.Add(offset);
        }
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private LyricsEditorView? _editor;

    /// <summary>Открыт редактор текста: «Назад» и Esc закрывают сначала его.</summary>
    public bool EditorOpen => _editor is not null;

    /// <summary>Редактор текста поверх «Сейчас играет»: панель плеера под ним остаётся.</summary>
    private void OpenEditor()
    {
        if (_lyrics.Track is not { } track || _editor is not null) return;
        _editor = new LyricsEditorView(track, _lyrics.Stored, _lyrics.InitialDraft());
        SetRowSpan(_editor, 2);
        _editor.Closed += CloseEditor;
        Children.Add(_editor);
    }

    private void CloseEditor()
    {
        if (_editor is null) return;
        Children.Remove(_editor);
        _editor = null;
    }

    /// <summary>«Назад» в редакторе: закрыть его, с несохранёнными правками — спросив.</summary>
    public Task CloseEditorAsync() => _editor?.CloseAsync() ?? Task.CompletedTask;

    private async Task FindAsync()
    {
        if (_lyrics.Track is not { } track) return;
        // Запрос — очищенные исполнитель и название, как ищет цепочка текстов
        var clean = TitleCleaner.Clean(track.Title, track.ArtistsText, track.AlbumId is not null || track.AlbumTitle is not null ? "song" : null);
        await LyricsSearchDialog.ShowAsync(XamlRoot, _lyrics, $"{clean.Artist ?? track.ArtistsText} {clean.Title}".Trim());
    }
}
