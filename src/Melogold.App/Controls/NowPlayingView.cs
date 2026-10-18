using System.Numerics;
using Melogold.App.Services;
using Melogold.Core.Artwork;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Melogold.App.Controls;

/// <summary>
/// «Сейчас играет» (docs/PROMPT.md §5.2): страница поверх окна, Esc закрывает её. Фон — размытая обложка с виньеткой
/// (<see cref="BackdropView"/>, tasks/0034), которая движется, пока играет трек и включён «Живой фон»; текст и кнопки
/// поверх него всегда светлые: палитра — тёмная схема цвета обложки (Material Color Utilities, как на Android), какая бы
/// ни была тема приложения. В широком окне — как в Linux: окно пополам, обложка с названием посередине левой половины
/// (сторона — min(0,50 · H, 0,38 · W)), текст — во всю правую; в узком — переключатель «Обложка · Текст».
/// Фон лежит под всем окном — и под строкой заголовка, и под панелью воспроизведения: они поверх него прозрачные и
/// светлые (<see cref="Insets"/> — их место). Управление остаётся в панели воспроизведения внизу.
/// </summary>
public sealed partial class NowPlayingView : Grid
{
    private const double WideWidth = 900;

    private readonly PlayerEngine _engine = App.Services.GetRequiredService<PlayerEngine>();
    private readonly LyricsService _lyrics = App.Services.GetRequiredService<LyricsService>();
    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private readonly SolidColorBrush _background = new(Colors.Transparent);
    private readonly BackdropView _backdrop = new();
    private readonly Windows.UI.ViewManagement.UISettings _ui = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _watch;
    private readonly Grid _body = new();
    private readonly Grid _top = new() { Padding = new Thickness(16, 8, 16, 0), RequestedTheme = ElementTheme.Dark };
    private Thickness _insets;
    private readonly Grid _artPanel = new() { RowSpacing = 16, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
    private readonly Grid _lyricsPanel = new();
    private readonly Border _artwork = new() { CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Image _artworkImage = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// Маленькая обложка из панели плеера — под большой, пока та скачивается: она уже в кэше, и «Сейчас играет» не
    /// открывается с пустым местом (после обновления и у новых треков большая обложка приходит не сразу).
    /// </summary>
    private readonly Image _artworkPlaceholder = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SelectorBar _mode = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly SyncedLyricsView _synced = new();
    private readonly ScrollViewer _plainScroller = new() { Padding = new Thickness(16, 0, 16, 0) };
    private readonly TextBlock _plain = new() { FontSize = 22, LineHeight = 34, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, 24, 0, 48) };
    private readonly StackPanel _message = new() { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _source = new() { FontSize = 12, Margin = new Thickness(16, 8, 16, 0) };
    private readonly List<TextBlock> _secondaryTexts = [];
    private ArtworkPalette? _palette;
    private string? _paletteKey;
    private bool _showLyricsInNarrow = true;
    /// <summary>
    /// Обложка — широкий кадр видео: показывается целиком, прямоугольником 16:9, а не квадратным куском. Решает сама
    /// картинка, а не адрес: у видео-«статики» кадр в кэше уже без чёрных полей и квадратный (VideoFrames).
    /// </summary>
    private bool _wideArtwork;
    private CancellationTokenSource? _paletteLoad;
    private bool _highContrast;


    public NowPlayingView()
    {
        Visibility = Visibility.Collapsed;
        Background = _background;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Фон — во всю область, под всем остальным. Шейдер (tasks/0035) берёт звук из захвата своего процесса, позицию и громкость —
        // у плеера: так он рисует то, что звучит сейчас
        var levels = App.Services.GetRequiredService<AudioLevels>();
        _backdrop.Audio = levels;
        _backdrop.Position = () => PlayerSeconds();
        _backdrop.Volume = () => _engine.OutputVolume;
        SetRowSpan(_backdrop, 2);
        Children.Add(_backdrop);

        // Верх: переключатель «Обложка · Текст» в узком окне. Закрывают «Назад» в заголовке окна и Esc — своей
        // стрелки нет (пользователь: два одинаковых действия друг над другом); меню текста — в «…» панели плеера
        // Текст и кнопки поверх размытой обложки светлые при любой теме приложения: тёмная тема стоит на верхней строке и
        // на теле «Сейчас играет», а не на редакторе текста поверх них (он остаётся в теме приложения)
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("NowPlayingArtwork"), Tag = "art" });
        _mode.Items.Add(new SelectorBarItem { Text = Loc.Get("PlayerLyrics"), Tag = "lyrics" });
        _mode.SelectionChanged += (_, _) =>
        {
            _showLyricsInNarrow = (string?)_mode.SelectedItem?.Tag == "lyrics";
            Arrange();
        };
        // Посередине окна, а не колонки между кнопками
        SetColumnSpan(_mode, 3);
        _top.Children.Add(_mode);
        Children.Add(_top);

        // Обложка, название, исполнитель
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _artwork.Child = new Grid { Children = { _artworkPlaceholder, _artworkImage } };
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

        _body.RequestedTheme = ElementTheme.Dark;
        _body.Padding = new Thickness(48, 16, 48, 24);
        _body.Children.Add(_artPanel);
        _body.Children.Add(_lyricsPanel);
        SetRow(_body, 1);
        Children.Add(_body);

        SizeChanged += (_, _) =>
        {
            _resizedAt = DateTime.UtcNow;
            Arrange();
        };
        // Часы фона: идут, только пока играет трек, окно видно, «Живой фон» включён и в Windows не выключены эффекты
        // анимации. Окно и системная настройка событий не дают — раз в секунду проверяем, пока «Сейчас играет» открыт
        _watch = DispatcherQueue.CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(1);
        _watch.Tick += (_, _) => UpdateBackdropClock();
        _engine.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateBackdropClock);
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsStore.LiveBackground)) DispatcherQueue.TryEnqueue(UpdateBackdropClock);
        };
        // Обложка сдвинулась (размер окна, кадр видео вместо квадрата, перенос названия) — текущая строка за ней
        _artwork.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(AnchorLyrics);
        _artPanel.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(AnchorLyrics);
        _synced.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(AnchorLyrics);
        _engine.TrackChanged += OnTrackChanged;
        // Своё название, исполнитель или альбом (tasks/0011) — сразу и здесь, не после закрытия и открытия
        App.Services.GetRequiredService<Library>().Changed += change =>
        {
            if (change.HasFlag(LibraryChange.Overrides)) DispatcherQueue.TryEnqueue(() =>
            {
                if (IsOpen) ShowNames();
            });
        };
        _lyrics.Changed += ShowLyrics;
        ActualThemeChanged += (_, _) => _ = LoadPaletteAsync(force: true);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Место строки заголовка (сверху), панели воспроизведения (снизу) и очереди (справа): фон лежит и под ними, а обложка
    /// и текст — между ними.
    /// </summary>
    public Thickness Insets
    {
        get => _insets;
        set
        {
            if (_insets == value) return;
            _insets = value;
            _top.Margin = new Thickness(value.Left, value.Top, value.Right, 0);
            _body.Margin = new Thickness(value.Left, 0, value.Right, value.Bottom);
            if (_editor is not null) _editor.Margin = value;
            Arrange();
        }
    }

    /// <summary>Цвета обложки сменились: панель воспроизведения красит ими ▶ и ползунок.</summary>
    public event Action<ArtworkPalette>? PaletteChanged;

    public ArtworkPalette? Palette => _palette;

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
        if (_palette is null) ApplyPalette(ThemePalette(), animate: false);
        ShowTrack();
        ShowLyrics();
        _synced.Start();
        UpdateBackdropClock();
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
        UpdateBackdropClock();
        Animate(opening: false);
        OpenChanged?.Invoke(false);
    }

    public void Toggle(bool lyrics = false, UIElement? from = null)
    {
        if (IsOpen) Close();
        else Open(lyrics, from);
    }

    /// <summary>Фон только проявляется — он и под строкой заголовка, и под панелью, сдвиг открыл бы край; обложка и текст ещё и подъезжают.</summary>
    private void Animate(bool opening)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        if (!AnimationsEnabled)
        {
            if (!opening) Visibility = Visibility.Collapsed;
            return;
        }
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
        foreach (var content in new UIElement[] { _top, _body })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(content, true);
            ElementCompositionPreview.GetElementVisual(content).StartAnimation("Translation", move);
        }
        visual.StartAnimation("Opacity", fade);
        batch.End();
        if (!opening) batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_lyrics.Active) Visibility = Visibility.Collapsed;
        });
    }

    private void OnTrackChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsOpen)
        {
            // Цвет по обложке — заранее: к открытию «Сейчас играет» фон уже готов, а не появляется после текста
            if (_engine.Current is not null) _ = LoadPaletteAsync(force: false);
            return;
        }
        if (_engine.Current is null)
        {
            Close();
            return;
        }
        ShowTrack();
    });

    /// <summary>Название и исполнитель — со своими правками (tasks/0011).</summary>
    private void ShowNames()
    {
        if (_engine.Current is not { } original) return;
        var track = App.Services.GetRequiredService<Library>().Display(original);
        _title.Text = track.Title;
        _artist.Text = track.ArtistsText ?? "";
        AutomationProperties.SetName(this, $"{Loc.Get("NowPlaying")}: {track.Title}");
    }

    private void ShowTrack()
    {
        if (_engine.Current is not { } original) return;
        var track = App.Services.GetRequiredService<Library>().Display(original);
        ShowNames();
        var url = track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId);
        // Пока большая обложка не открылась — маленькая, та же, что в панели плеера
        _artworkPlaceholder.Source = Images.Player(Thumbnails.Sized(url, 112));
        var artwork = Thumbnails.Sized(url, 720);
        var image = Images.From(artwork);
        _artworkImage.Source = image;
        if (image is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap)
            bitmap.ImageOpened += (_, _) =>
            {
                if (_artworkImage.Source != bitmap || bitmap.PixelHeight <= 0) return;
                _artworkPlaceholder.Source = null;
                SetWide(bitmap.PixelWidth > bitmap.PixelHeight * 1.2);
            };
        else SetWide(false);
        _ = LoadPaletteAsync(force: false);
    }

    private void SetWide(bool wide)
    {
        if (_wideArtwork == wide) return;
        _wideArtwork = wide;
        Arrange();
    }

    /// <summary>Цвета тёмной темы: пока цвет по обложке не посчитан и у серой обложки. Фон «Сейчас играет» тёмный всегда.</summary>
    private static ArtworkPalette ThemePalette() =>
        new(Color.FromArgb(255, 0x20, 0x20, 0x20), Colors.White, Color.FromArgb(255, 0xC5, 0xC5, 0xC5), Color.FromArgb(255, 0x3A, 0x3A, 0x3A),
            Color.FromArgb(255, 0xF0, 0xF0, 0xF0), Color.FromArgb(255, 0x20, 0x20, 0x20));

    /// <summary>
    /// Цвета текста по обложке и сам фон. Палитра — тёмная схема цвета обложки, какая бы ни была тема приложения; у серой
    /// обложки — тёмная тема. Фон никогда не прозрачный: пока обложка считается, стоит тёмная заливка, готовая размытая
    /// обложка наплывает за 0,4 с (пользователь: «фон появлялся дольше, чем наполнение»).
    /// </summary>
    private async Task LoadPaletteAsync(bool force)
    {
        if (_engine.Current is not { } track) return;
        if (!force && _paletteKey == track.VideoId) return;
        _paletteKey = track.VideoId;
        if (_palette is null) ApplyPalette(ThemePalette(), animate: false);
        _paletteLoad?.Cancel();
        var cancel = _paletteLoad = new CancellationTokenSource();
        var url = Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 226);
        // Высокая контрастность: цвета системы, без обложки и без оттенка
        _highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        _backdrop.Visibility = _highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (!_highContrast) _ = LoadBackdropAsync(track.VideoId, url, cancel.Token);
        var palette = _highContrast
            ? new ArtworkPalette(SystemColor("SystemColorWindowColor"), SystemColor("SystemColorWindowTextColor"), SystemColor("SystemColorWindowTextColor"), SystemColor("SystemColorHighlightColor"),
                SystemColor("SystemColorHighlightColor"), SystemColor("SystemColorHighlightTextColor"))
            : await ArtworkColors.PaletteAsync(track.VideoId, url, dark: true, cancel.Token);
        if (cancel.IsCancellationRequested) return;
        // Открыто — цвет наплывает; закрыто — просто встаёт, к открытию он уже на месте
        ApplyPalette(palette ?? ThemePalette(), animate: IsOpen && AnimationsEnabled);
    }

    /// <summary>Размытая обложка за текстом: готовится в фоне, один раз на обложку, и наплывает поверх прежней.</summary>
    private async Task LoadBackdropAsync(string key, string? url, CancellationToken ct)
    {
        var cover = await ArtworkColors.BackdropAsync(key, url, ct);
        if (ct.IsCancellationRequested || cover is null) return;
        await _backdrop.ShowAsync(key, cover, animate: IsOpen && AnimationsEnabled);
    }

    /// <summary>
    /// Часы фона идут, только пока «Сейчас играет» на экране, трек играет, окно видно, «Живой фон» включён и в системе не
    /// выключены эффекты анимации. Иначе стоят: фон тот же кадр, но неподвижный; пауза время не сбрасывает.
    /// </summary>
    private void UpdateBackdropClock()
    {
        var showing = IsOpen && _lyrics.Active;
        // Высокая контрастность — фона нет вовсе. Живой — шейдер рисует кадры; не живой — замерший кадр
        var live = showing && !_highContrast && BackdropClock.IsLive(WindowVisible, _settings.LiveBackground, _ui.AnimationsEnabled);
        _backdrop.SetMotion(live, _engine.IsPlaying);
        if (showing && !_watch.IsRunning) _watch.Start();
        else if (!showing) _watch.Stop();
    }

    /// <summary>Позиция плеера, секунды; null — плеер закрыт или позиции нет.</summary>
    private double? PlayerSeconds()
    {
        try
        {
            return _engine.Position.TotalSeconds;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Окно на экране: не скрыто (открыт мини-плеер) и не свёрнуто.</summary>
    private static bool WindowVisible =>
        App.Current?.Window?.AppWindow is not { } window
        || window.IsVisible && window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _backgroundFade;

    private void ApplyPalette(ArtworkPalette palette, bool animate)
    {
        _palette = palette;
        _backgroundFade?.Stop();
        if (animate)
        {
            var fade = new Microsoft.UI.Xaml.Media.Animation.ColorAnimation
            {
                To = palette.Background,
                Duration = TimeSpan.FromMilliseconds(300),
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _background);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Color");
            _backgroundFade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            _backgroundFade.Children.Add(fade);
            _backgroundFade.Completed += (_, _) => _background.Color = palette.Background;
            _backgroundFade.Begin();
        }
        else _background.Color = palette.Background;
        _title.Foreground = new SolidColorBrush(_palette.Text);
        _plain.Foreground = new SolidColorBrush(_palette.Text);
        foreach (var text in _secondaryTexts) text.Foreground = new SolidColorBrush(_palette.SecondaryText);
        foreach (var text in _message.Children.OfType<TextBlock>()) text.Foreground = new SolidColorBrush(_palette.Text);
        _synced.SetColors(_palette.Text, _palette.Pill);
        PaletteChanged?.Invoke(palette);
    }

    /// <summary>Текст, который нажимается: без подчёркивания и цвета ссылки, цвет — от обложки.</summary>
    private static HyperlinkButton Link(TextBlock text, Action<FrameworkElement> open)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += (_, _) => open(link);
        return link;
    }

    private static Color SystemColor(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Color color ? color : Colors.Black;

    private DateTime _resizedAt;

    /// <summary>
    /// Широкое окно (обложка слева, текст справа): середина текущей строки — на уровне середины обложки, как в Linux
    /// (пользователь: «это решение для ноутбуков»); при смене размера окна — сразу, при прочих сдвигах — плавно. Узкое —
    /// строка у верха (tasks/0013).
    /// </summary>
    private void AnchorLyrics()
    {
        var immediate = DateTime.UtcNow - _resizedAt < TimeSpan.FromMilliseconds(400);
        if (ActualWidth >= WideWidth && _artPanel.Visibility == Visibility.Visible && _artwork.ActualHeight > 0 && _synced.ActualHeight > 0)
        {
            var center = _artwork.TransformToVisual(_synced).TransformPoint(new Windows.Foundation.Point(0, _artwork.ActualHeight / 2)).Y;
            _synced.SetAnchor(center, immediate);
        }
        else _synced.SetAnchor(null, immediate);
    }

    /// <summary>Широкое окно — обложка и текст рядом; узкое — одно из двух по переключателю.</summary>
    /// <summary>Ширина области «Сейчас играет» без очереди справа.</summary>
    private double AreaWidth => Math.Max(0, ActualWidth - _insets.Right);

    /// <summary>Высота области вместе со строкой заголовка, как у страницы Linux с её шапкой, но без панели воспроизведения.</summary>
    private double AreaHeight => Math.Max(0, ActualHeight - _insets.Bottom);

    private void Arrange()
    {
        var (width, height) = (AreaWidth, AreaHeight);
        var wide = width >= WideWidth;
        _mode.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _body.ColumnDefinitions.Clear();
        double side;
        if (wide)
        {
            // Как в Linux (now_playing.rs): окно пополам; слева колонка не шире стороны обложки + 2 × 28 посередине половины,
            // справа текст во всю половину (поле справа 12). Сторона обложки — min(0,50 · H, 0,38 · W) (tasks/0034)
            _body.Padding = new Thickness(0, 16, 12, 24);
            var half = width / 2;
            side = Math.Max(120, Math.Min(NowPlayingMetrics.WideCoverSide(width, height), half - 2 * 28));
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _body.HorizontalAlignment = HorizontalAlignment.Stretch;
            _artPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _artPanel.MaxWidth = double.PositiveInfinity;
            // Ширина — ровно обложка: длинное название переносится под ней
            _artPanel.Width = side;
            SetColumn(_lyricsPanel, 1);
            _artPanel.Visibility = Visibility.Visible;
            _lyricsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _body.Padding = new Thickness(48, 16, 48, 24);
            var available = width - 96;
            side = Math.Max(120, Math.Min(Math.Min(available, 480), height - 220));
            _body.HorizontalAlignment = HorizontalAlignment.Stretch;
            _artPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _artPanel.MaxWidth = 480;
            _artPanel.Width = double.NaN;
            SetColumn(_lyricsPanel, 0);
            _artPanel.Visibility = _showLyricsInNarrow ? Visibility.Collapsed : Visibility.Visible;
            _lyricsPanel.Visibility = _showLyricsInNarrow ? Visibility.Visible : Visibility.Collapsed;
        }
        _artwork.Width = side;
        _artwork.Height = _wideArtwork ? Math.Round(side * 9 / 16) : side;
        ApplyLyricsSize(wide);
    }

    /// <summary>
    /// Размер текста песни — от высоты всей области «Сейчас играет», а не колонки текста (tasks/0034 §3): в окне любой ширины
    /// видно одно и то же число строк. Обычный текст — в тех же пропорциях. В широкой раскладке строка не шире ~28 знаков и
    /// стоит у левого края своей колонки.
    /// </summary>
    private void ApplyLyricsSize(bool wide)
    {
        var size = NowPlayingMetrics.LyricsFontSize(AreaHeight);
        _synced.AreaHeight = AreaHeight;
        _plain.FontSize = size * 22 / 28;
        _plain.LineHeight = size * 34 / 28;
        // Текст — во всю свою половину, как в Linux: строки переносятся по её ширине
        foreach (FrameworkElement view in new FrameworkElement[] { _synced, _plainScroller })
        {
            view.MaxWidth = double.PositiveInfinity;
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
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
        // Между строкой заголовка и панелью воспроизведения, как обложка и текст
        _editor.Margin = _insets;
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
        if (_lyrics.Track is not { } original) return;
        // Запрос — очищенные исполнитель и название, как ищет цепочка текстов; свои названия — первыми (tasks/0011)
        var track = App.Services.GetRequiredService<Library>().Display(original);
        var clean = TitleCleaner.Clean(track.Title, track.ArtistsText, track.AlbumId is not null || track.AlbumTitle is not null ? "song" : null);
        await LyricsSearchDialog.ShowAsync(XamlRoot, _lyrics, $"{clean.Artist ?? track.ArtistsText} {clean.Title}".Trim());
    }
}
