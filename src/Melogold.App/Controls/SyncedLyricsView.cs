using System.Numerics;
using Melogold.App.Services;
using Melogold.Core.Lyrics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Melogold.App.Controls;

/// <summary>
/// Синхронный текст (docs/PROMPT.md §5.2, Android <c>SyncedLyricsView.kt</c>, <c>spec/lyrics.md</c>):
/// <list type="bullet">
/// <item>активную строку отмечает подложка, которая переезжает на пружине; пройденные строки приглушены сильнее будущих;</item>
/// <item>при времени слов строка загорается слово за словом;</item>
/// <item>клик по строке перематывает; ручная прокрутка останавливает слежение на 3 с, есть «К текущей строке»;</item>
/// <item>в паузе без слов точки есть только на время паузы: выскакивают по одной и лопаются перед следующей строкой;</item>
/// <item>вторая сторона дуэта — у правого края, подпевка мельче под строкой, перевод — под текстом.</item>
/// </list>
/// </summary>
public sealed partial class SyncedLyricsView : Grid
{
    /// <summary>Текст чуть опережает звук: слово загорается ровно тогда, когда его поют.</summary>
    private const long LeadMs = 60;

    private const double PastOpacity = 0.35;
    private const double FutureOpacity = 0.6;
    private const byte UnsungAlpha = 102; // 0.4
    private static readonly TimeSpan ResumeFollow = TimeSpan.FromSeconds(3);

    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly Grid _content = new();
    private readonly Canvas _pillHost = new() { IsHitTestVisible = false };
    private readonly StackPanel _lines = new() { Spacing = 4 };
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _topFade = new() { Height = 32, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _bottomFade = new() { Height = 72, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Button _jump = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16), Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _clock;
    private readonly DispatcherQueueTimer _resume;
    private readonly bool _animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private readonly List<RowView> _rowViews = [];
    private IReadOnlyList<LyricRow> _rows = [];
    private long _offset;
    private int _active = -2;
    private bool _follow = true;
    private DateTime _programmaticUntil;
    private Color _text = Colors.White;
    private Color _pill = Color.FromArgb(40, 255, 255, 255);

    private Compositor? _compositor;
    private ShapeVisual? _pillVisual;
    private CompositionSpriteShape? _pillShape;
    private CompositionRoundedRectangleGeometry? _pillGeometry;
    private CompositionColorBrush? _pillBrush;

    public SyncedLyricsView()
    {
        _content.Padding = new Thickness(0, 0, 0, 0);
        _content.Children.Add(_pillHost);
        _content.Children.Add(_lines);
        _scroller.Content = _content;
        Children.Add(_scroller);
        // Строки уходят в фон у краёв, а не обрезаются
        Children.Add(_topFade);
        Children.Add(_bottomFade);
        _jump.Content = Loc.Get("LyricsJumpToLine");
        _jump.Click += (_, _) => Resume();
        Children.Add(_jump);

        var queue = DispatcherQueue.GetForCurrentThread();
        _clock = queue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(33);
        _clock.Tick += (_, _) => Tick();
        _resume = queue.CreateTimer();
        _resume.Interval = ResumeFollow;
        _resume.IsRepeating = false;
        _resume.Tick += (_, _) => Resume();

        _scroller.ViewChanging += (_, _) =>
        {
            if (DateTime.UtcNow < _programmaticUntil) return;
            // Прокрутил человек: слежение — через 3 с после последнего движения
            _follow = false;
            _jump.Visibility = Visibility.Visible;
            _resume.Stop();
            _resume.Start();
        };
        _scroller.SizeChanged += (_, _) =>
        {
            // Первая строка встаёт на треть экрана, последняя может дойти до неё
            _lines.Margin = new Thickness(0, _scroller.ActualHeight * 0.3, 0, _scroller.ActualHeight * 0.6);
            DispatcherQueue.TryEnqueue(() => MovePill(immediate: true));
        };
        _content.SizeChanged += (_, _) =>
        {
            if (_pillVisual is not null) _pillVisual.Size = new Vector2((float)_content.ActualWidth, (float)_content.ActualHeight);
        };
        Loaded += (_, _) => EnsurePill();
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _resume.Stop();
        };
    }

    /// <summary>Позиция звука, мс.</summary>
    public Func<long> Position { get; set; } = () => 0;

    public Func<bool> IsPlaying { get; set; } = () => false;

    /// <summary>Перемотка к строке (позиция звука, мс).</summary>
    public Action<long>? Seek { get; set; }

    /// <summary>Цвета текста, подложки и фона под текстом (из обложки на «Сейчас играет»).</summary>
    public void SetColors(Color text, Color pill, Color background)
    {
        _text = text;
        _pill = pill;
        _topFade.Fill = Fade(background, top: true);
        _bottomFade.Fill = Fade(background, top: false);
        if (_pillBrush is not null) _pillBrush.Color = pill;
        foreach (var row in _rowViews) row.ApplyColor(text);
        _active = -2;
        Tick();
    }

    private static LinearGradientBrush Fade(Color background, bool top)
    {
        var clear = Color.FromArgb(0, background.R, background.G, background.B);
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = top ? background : clear, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = top ? clear : background, Offset = 1 });
        return brush;
    }

    public void SetLyrics(IReadOnlyList<LyricRow> rows, long offsetMs)
    {
        var same = ReferenceEquals(rows, _rows);
        _offset = offsetMs;
        if (same) return;
        _rows = rows;
        _lines.Children.Clear();
        _rowViews.Clear();
        foreach (var row in rows)
        {
            var view = row is LyricRow.Sung sung ? RowView.ForLine(sung, _text) : RowView.ForInterlude((LyricRow.Interlude)row, _text);
            view.Element.Tapped += (_, _) => Seek?.Invoke(Math.Max(0, row.StartMs - _offset));
            _rowViews.Add(view);
            _lines.Children.Add(view.Element);
        }
        _active = -2;
        _follow = true;
        _jump.Visibility = Visibility.Collapsed;
        _scroller.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(Tick);
    }

    public void Start() => _clock.Start();

    public void Stop() => _clock.Stop();

    private long LyricsPosition => Position() + LeadMs + _offset;

    private void Resume()
    {
        _resume.Stop();
        _follow = true;
        _jump.Visibility = Visibility.Collapsed;
        ScrollToActive(animated: true);
    }

    private void Tick()
    {
        if (_rows.Count == 0) return;
        var position = LyricsPosition;
        var index = LyricRows.ActiveIndexAt(_rows, position);
        if (index != _active)
        {
            var previous = _active;
            _active = index;
            for (var i = 0; i < _rowViews.Count; i++)
                _rowViews[i].SetState(i == index, i < index ? PastOpacity : i == index ? 1 : FutureOpacity, _animations);
            // Проигрыш занимает место только пока идёт: раскладка меняется, подложка и прокрутка — после неё
            DispatcherQueue.TryEnqueue(() =>
            {
                MovePill(immediate: previous < -1);
                if (_follow) ScrollToActive(animated: previous >= -1);
            });
        }
        if (index >= 0) _rowViews[index].Update(position, IsPlaying(), _animations, _text);
    }

    private void ScrollToActive(bool animated)
    {
        var target = _active >= 0 ? _rowViews[_active].Element : null;
        double y = 0;
        if (target is not null && target.ActualHeight > 0)
            y = target.TransformToVisual(_content).TransformPoint(default).Y - _scroller.ActualHeight * 0.3;
        y = Math.Clamp(y, 0, _scroller.ScrollableHeight);
        if (Math.Abs(y - _scroller.VerticalOffset) < 1) return;
        _programmaticUntil = DateTime.UtcNow.AddMilliseconds(animated ? 900 : 200);
        _scroller.ChangeView(null, y, null, !animated || !_animations);
    }

    // ---------- Подложка (Composition, пружина) ----------

    private void EnsurePill()
    {
        if (_pillVisual is not null) return;
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _pillGeometry = _compositor.CreateRoundedRectangleGeometry();
        _pillGeometry.CornerRadius = new Vector2(12, 12);
        _pillBrush = _compositor.CreateColorBrush(_pill);
        _pillShape = _compositor.CreateSpriteShape(_pillGeometry);
        _pillShape.FillBrush = _pillBrush;
        _pillVisual = _compositor.CreateShapeVisual();
        _pillVisual.Shapes.Add(_pillShape);
        _pillVisual.Size = new Vector2((float)_content.ActualWidth, (float)_content.ActualHeight);
        _pillVisual.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(_pillHost, _pillVisual);
        MovePill(immediate: true);
    }

    private void MovePill(bool immediate)
    {
        if (_pillVisual is null || _pillShape is null || _pillGeometry is null || _compositor is null) return;
        var view = _active >= 0 && _active < _rowViews.Count ? _rowViews[_active] : null;
        if (view is null || view.IsInterlude || view.Body.ActualWidth <= 0)
        {
            Fade(0);
            return;
        }
        var origin = view.Body.TransformToVisual(_content).TransformPoint(default);
        var offset = new Vector2((float)(origin.X - 16), (float)(origin.Y - 10));
        var size = new Vector2((float)view.Body.ActualWidth + 32, (float)view.Body.ActualHeight + 20);
        if (immediate || !_animations || _pillVisual.Opacity < 0.01f)
        {
            _pillShape.StopAnimation("Offset");
            _pillGeometry.StopAnimation("Size");
            _pillShape.Offset = offset;
            _pillGeometry.Size = size;
        }
        else
        {
            _pillShape.StartAnimation("Offset", Spring(offset));
            _pillGeometry.StartAnimation("Size", Spring(size));
        }
        Fade(1);
    }

    private SpringVector2NaturalMotionAnimation Spring(Vector2 target)
    {
        var spring = _compositor!.CreateSpringVector2Animation();
        spring.FinalValue = target;
        spring.DampingRatio = 0.78f;
        spring.Period = TimeSpan.FromMilliseconds(70);
        return spring;
    }

    private void Fade(float to)
    {
        if (_pillVisual is null || _compositor is null || Math.Abs(_pillVisual.Opacity - to) < 0.01f) return;
        if (!_animations)
        {
            _pillVisual.Opacity = to;
            return;
        }
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        _pillVisual.StartAnimation("Opacity", fade);
    }

    /// <summary>Строка экрана: спетая строка или проигрыш.</summary>
    private sealed class RowView
    {
        private readonly SyncedLine? _line;
        private readonly LyricRow.Interlude? _interlude;
        private readonly TextBlock? _main;
        private readonly List<(Run Run, SyncedWord Word)> _words = [];
        private readonly StackPanel? _dots;
        private readonly Ellipse[] _dotShapes = new Ellipse[3];
        private readonly ScaleTransform _groupScale = new();
        private readonly ScaleTransform[] _dotScales = new ScaleTransform[3];
        private int _sungWords = -1;
        private DateTime _activatedAt;
        private bool _active;

        private RowView(FrameworkElement element, FrameworkElement body)
        {
            Element = element;
            Body = body;
        }

        public FrameworkElement Element { get; }

        /// <summary>То, что обводит подложка.</summary>
        public FrameworkElement Body { get; }

        public bool IsInterlude => _interlude is not null;

        private RowView(FrameworkElement element, FrameworkElement body, SyncedLine line, TextBlock main) : this(element, body)
        {
            _line = line;
            _main = main;
        }

        private RowView(FrameworkElement element, FrameworkElement body, LyricRow.Interlude interlude, StackPanel dots) : this(element, body)
        {
            _interlude = interlude;
            _dots = dots;
        }

        public static RowView ForLine(LyricRow.Sung row, Color color)
        {
            var line = row.Line;
            var end = line.Side == VocalSide.End;
            var main = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                LineHeight = 36,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = end ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = end ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var body = new StackPanel { Spacing = 2, HorizontalAlignment = main.HorizontalAlignment };
            body.Children.Add(main);
            if (line.Background is { } backing)
                body.Children.Add(new TextBlock { Text = backing.Text, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = main.TextAlignment, HorizontalAlignment = main.HorizontalAlignment, Opacity = 0.8 });
            if ((line.Translation ?? line.Transliteration) is { } extra)
                body.Children.Add(new TextBlock { Text = extra, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = main.TextAlignment, HorizontalAlignment = main.HorizontalAlignment, Opacity = 0.75 });
            var element = new Border { Padding = new Thickness(16, 10, 16, 10), Child = body, Background = new SolidColorBrush(Colors.Transparent), Opacity = FutureOpacity };
            AutomationProperties.SetName(element, line.Text);
            var view = new RowView(element, body, line, main);
            if (line.Words.Count > 0)
            {
                foreach (var word in line.Words)
                {
                    var run = new Run { Text = word.Text };
                    main.Inlines.Add(run);
                    view._words.Add((run, word));
                }
            }
            else main.Text = line.Text;
            view.ApplyColor(color);
            return view;
        }

        public static RowView ForInterlude(LyricRow.Interlude row, Color color)
        {
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = row.Side == VocalSide.End ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(32, 0, 32, 0),
            };
            var element = new Grid { Height = 0, Background = new SolidColorBrush(Colors.Transparent) };
            element.Children.Add(dots);
            AutomationProperties.SetName(element, Loc.Get("LyricsInstrumental"));
            var view = new RowView(element, dots, row, dots);
            dots.RenderTransform = view._groupScale;
            dots.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            for (var k = 0; k < 3; k++)
            {
                view._dotScales[k] = new ScaleTransform { ScaleX = 0, ScaleY = 0 };
                view._dotShapes[k] = new Ellipse { Width = 10, Height = 10, RenderTransform = view._dotScales[k], RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
                dots.Children.Add(view._dotShapes[k]);
            }
            view.ApplyColor(color);
            return view;
        }

        public void ApplyColor(Color color)
        {
            if (_main is not null)
            {
                foreach (var child in ((Panel)Body).Children.OfType<TextBlock>()) child.Foreground = new SolidColorBrush(color);
                _sungWords = -1;
                foreach (var (run, _) in _words) run.Foreground = new SolidColorBrush(color);
            }
            foreach (var dot in _dotShapes) if (dot is not null) dot.Fill = new SolidColorBrush(color);
        }

        public void SetState(bool active, double opacity, bool animations)
        {
            _active = active;
            Element.OpacityTransition = animations ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) } : null;
            Element.Opacity = opacity;
            if (_interlude is not null)
            {
                // Проигрыш занимает место только пока играет
                ((Grid)Element).Height = active ? 40 : 0;
                _activatedAt = DateTime.UtcNow;
                if (!active) foreach (var s in _dotScales) s.ScaleX = s.ScaleY = 0;
            }
            if (_words.Count > 0)
            {
                _sungWords = -1;
                // Вне строки слова одного цвета: приглушает прозрачность строки
                if (!active) foreach (var (run, _) in _words) ((SolidColorBrush)run.Foreground).Color = WithAlpha(((SolidColorBrush)run.Foreground).Color, 255);
            }
        }

        /// <summary>Кадр активной строки: слова загораются по времени, точки проигрыша живут.</summary>
        public void Update(long position, bool playing, bool animations, Color color)
        {
            if (!_active) return;
            if (_words.Count > 0)
            {
                var sung = _words.Count(w => w.Word.StartMs <= position);
                if (sung == _sungWords) return;
                _sungWords = sung;
                for (var i = 0; i < _words.Count; i++) _words[i].Run.Foreground = new SolidColorBrush(i < sung ? color : WithAlpha(color, UnsungAlpha));
                return;
            }
            if (_interlude is not { } row) return;
            var clock = (DateTime.UtcNow - _activatedAt).TotalMilliseconds;
            var duration = Math.Max(1, row.EndMs - row.StartMs);
            var fraction = Math.Clamp((position - row.StartMs) / (double)duration, 0, 1);
            var remaining = row.EndMs - position;
            var animated = animations && playing;
            var groupScale = animated ? 1 + 0.075 * (1 - Math.Cos(2 * Math.PI * clock / 3000)) : 1;
            var groupAlpha = 1.0;
            if (animated && remaining < 1000)
            {
                // Перед следующей строкой точки раздуваются и лопаются
                if (remaining > 250)
                {
                    var p = Math.Clamp((1000 - remaining) / 750.0, 0, 1);
                    groupScale += (1.25 - groupScale) * (1 - (1 - p) * (1 - p));
                }
                else
                {
                    var p = Math.Clamp((250 - remaining) / 250.0, 0, 1);
                    groupScale = 1.25 + (0.4 - 1.25) * p;
                    groupAlpha = 1 - p;
                }
            }
            _groupScale.ScaleX = _groupScale.ScaleY = groupScale;
            for (var k = 0; k < 3; k++)
            {
                var fill = Math.Clamp(fraction * 3 - k, 0, 1);
                // Появление: каждая точка выскакивает через 90 мс после предыдущей, с лёгким перелётом
                var appear = animated ? EaseOutBack(Math.Clamp((clock - k * 90) / 320, 0, 1)) : 1;
                _dotScales[k].ScaleX = _dotScales[k].ScaleY = appear;
                _dotShapes[k].Opacity = Math.Clamp((0.2 + 0.7 * fill) * groupAlpha, 0, 1);
            }
        }

        private static double EaseOutBack(double t)
        {
            const double c1 = 1.70158, c3 = c1 + 1;
            var u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }

        private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
