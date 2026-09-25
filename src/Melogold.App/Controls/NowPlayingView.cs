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
    private readonly Grid _body = new() { ColumnSpacing = 48 };
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
    private readonly Button _menuButton = new();
    private readonly MenuFlyout _menu = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
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

        // Верх: «Свернуть», переключатель в узком окне, меню текста
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
        SetColumn(_mode, 1);
        top.Children.Add(_mode);
        _menuButton.Content = new FontIcon { Glyph = "", FontSize = 16 };
        _menuButton.Style = (Style)Application.Current.Resources["PlayerIconButtonStyle"];
        AutomationProperties.SetName(_menuButton, Loc.Get("LyricsMenu"));
        ToolTipService.SetToolTip(_menuButton, Loc.Get("LyricsMenu"));
        _menuButton.Flyout = _menu;
        SetColumn(_menuButton, 2);
        top.Children.Add(_menuButton);
        Children.Add(top);

        // Обложка, название, исполнитель
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artwork.Child = _artworkImage;
        _artPanel.Children.Add(_artwork);
        var names = new StackPanel { Spacing = 4 };
        names.Children.Add(_title);
        names.Children.Add(_artist);
        _secondaryTexts.Add(_artist);
        SetRow(names, 1);
        _artPanel.Children.Add(names);

        // Текст: синхронный, обычный или сообщение
        _lyricsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _lyricsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _plainScroller.Content = _plain;
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

    private static Color SystemColor(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Color color ? color : Colors.Black;

    /// <summary>Широкое окно — обложка и текст рядом; узкое — одно из двух по переключателю.</summary>
    private void Arrange()
    {
        var wide = ActualWidth >= WideWidth;
        _mode.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _body.ColumnDefinitions.Clear();
        if (wide)
        {
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            SetColumn(_lyricsPanel, 1);
            _artPanel.Visibility = Visibility.Visible;
            _lyricsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SetColumn(_lyricsPanel, 0);
            _artPanel.Visibility = _showLyricsInNarrow ? Visibility.Collapsed : Visibility.Visible;
            _lyricsPanel.Visibility = _showLyricsInNarrow ? Visibility.Visible : Visibility.Collapsed;
        }
        // Обложка — квадрат по ширине колонки, но не выше окна
        var column = wide ? (ActualWidth - 96 - 48) * 2 / 5 : ActualWidth - 96;
        var side = Math.Max(120, Math.Min(Math.Min(column, 480), ActualHeight - 220));
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
            _ => "",
        };
        BuildMenu();
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
    /// Меню текста (§5.2): переключатель подписан по тому, что на экране; «Найти текст» — LRCLIB и файл; сдвиг
    /// ±0,1 и ±0,5 с. Собирается заранее, при смене текста, — при открытии меню не дёргается.
    /// </summary>
    private void BuildMenu()
    {
        _menu.Items.Clear();
        var state = _lyrics.State;
        if (state is LyricsState.Synced)
            _menu.Items.Add(Item(Loc.Get("LyricsShowPlain"), "", _lyrics.ToggleSynced));
        else if (state is LyricsState.Plain && _lyrics.HasSynced)
            _menu.Items.Add(Item(Loc.Get("LyricsShowSynced"), "", _lyrics.ToggleSynced));
        _menu.Items.Add(Item(Loc.Get("LyricsFindMenu"), "", () => _ = FindAsync()));
        if (state is LyricsState.Synced synced)
        {
            _menu.Items.Add(new MenuFlyoutSeparator());
            _menu.Items.Add(Item(Loc.Get("LyricsEarlier05"), null, () => _lyrics.Shift(500)));
            _menu.Items.Add(Item(Loc.Get("LyricsEarlier01"), null, () => _lyrics.Shift(100)));
            _menu.Items.Add(Item(Loc.Get("LyricsLater01"), null, () => _lyrics.Shift(-100)));
            _menu.Items.Add(Item(Loc.Get("LyricsLater05"), null, () => _lyrics.Shift(-500)));
            if (synced.OffsetMs != 0)
                _menu.Items.Add(Item(Loc.Format("LyricsResetShiftFormat", (synced.OffsetMs / 1000.0).ToString("+0.0;-0.0", System.Globalization.CultureInfo.CurrentCulture)), null, _lyrics.ResetShift));
        }
        _menuButton.IsEnabled = _lyrics.Track is not null;
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task FindAsync()
    {
        if (_lyrics.Track is not { } track) return;
        // Запрос — очищенные исполнитель и название, как ищет цепочка текстов
        var clean = TitleCleaner.Clean(track.Title, track.ArtistsText, track.AlbumId is not null || track.AlbumTitle is not null ? "song" : null);
        await LyricsSearchDialog.ShowAsync(XamlRoot, _lyrics, $"{clean.Artist ?? track.ArtistsText} {clean.Title}".Trim());
    }
}
