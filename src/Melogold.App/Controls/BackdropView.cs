using System.Diagnostics;
using System.Numerics;
using Melogold.App.Services;
using Melogold.Core.Artwork;
using Melogold.Core.Visualizer;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Melogold.App.Controls;

/// <summary>
/// Фон «Сейчас играет» (tasks/0034): размытая обложка двумя слоями, вращающимися навстречу, и виньетка. Нарисован
/// Composition: на слой — <c>SpriteVisual</c> с текстурой 24 × 24 (<see cref="BackdropCover"/>, матрица цвета уже в ней) под
/// <c>GaussianBlurEffect</c> (Win2D) с радиусом 32, текстура растянута с линейной фильтрацией. Положение слоёв — формулы
/// <see cref="BackdropMotion"/> от часов <see cref="BackdropClock"/>: часы идут, только пока <see cref="Running"/>; пауза
/// время не сбрасывает. Новая обложка наплывает поверх старой за 400 мс.
/// <para>
/// Фон «живой под музыку» (tasks/0035): шейдер на GPU (<see cref="FluidRenderer"/>, Direct3D 11 в <c>SwapChainPanel</c>) вместо
/// этих слоёв — жидкая обложка, капли-лава и пульс на бас. Кадр — по <c>CompositionTarget.Rendering</c>, то есть с частотой
/// монитора. Слои из tasks/0034 — запасной путь: он включается, если шейдер недоступен (нет устройства Direct3D, шейдер не
/// собрался, устройство потеряно). Движется шейдер, пока играет трек и фон живой (<see cref="SetMotion"/>); на паузе силы гаснут
/// за пару секунд и кадр замирает; «Живой фон» выключен, анимации выключены, окно скрыто — замерший кадр (силы 0, время стоит).
/// </para>
/// </summary>
public sealed class BackdropView : Grid
{
    /// <summary>σ гауссова размытия: радиус 32 логических пикселя, как у <c>GtkSnapshot.push_blur</c> (σ = радиус / 2).</summary>
    private const float BlurSigma = (float)(BackdropMotion.BlurRadius / 2);

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly BackdropClock _clock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DispatcherQueueTimer _timer;
    private readonly List<Slot> _slots = [];
    private TimeSpan _lastTick;
    private int _version;

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private SpriteVisual? _vignette;
    private CompositionRadialGradientBrush? _vignetteBrush;
    private CompositionEffectFactory? _blur;
    private bool _blurFailed;

    // Шейдер (tasks/0035)
    private SwapChainPanel? _panel;
    private FluidRenderer? _fluid;
    private FluidVisualizer? _visualizer;
    private BackdropCover? _cover;
    private bool _fluidFailed;
    private bool _live, _playing;
    private bool _subscribed;
    private TimeSpan? _lastFrame;

    public BackdropView()
    {
        IsHitTestVisible = false;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = FrameInterval;
        _timer.Tick += (_, _) => OnFrame();
        SizeChanged += (_, _) =>
        {
            Layout();
            if (_fluid is not null) RequestFrame();
        };
        Unloaded += (_, _) => Unsubscribe();
        Loaded += (_, _) => RequestFrame();
    }

    /// <summary>Звук для шейдера: измерения спектра и позиция плеера. Не задан — шейдер живёт без звука, спокойно.</summary>
    public AudioLevels? Audio { get; set; }

    /// <summary>Позиция плеера, секунды; null — неизвестна.</summary>
    public Func<double?>? Position { get; set; }

    /// <summary>Громкость плеера 0..1 (до «без звука» — 0).</summary>
    public Func<double>? Volume { get; set; }

    /// <summary>Фон рисует шейдер (а не слои из tasks/0034).</summary>
    public bool IsShader => _fluid is not null;

    /// <summary>Ключ обложки, которая сейчас на экране (или наплывает); null — фона ещё нет.</summary>
    public string? Key { get; private set; }

    /// <summary>Секунды часов анимации.</summary>
    public double Seconds => _clock.Seconds;

    private bool _running;

    /// <summary>Часы слоёв из tasks/0034 идут и фон движется. Остановка оставляет тот же кадр; запуск продолжает с того же места.</summary>
    private bool Running
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            if (value)
            {
                _lastTick = _stopwatch.Elapsed;
                _timer.Start();
            }
            else _timer.Stop();
        }
    }

    /// <summary>
    /// Состояние движения. <paramref name="live"/> — фон может двигаться (страница на экране, окно видно, «Живой фон» включён,
    /// анимации системы не выключены); <paramref name="playing"/> — трек играет. Живой и играет — кадры идут; живой и пауза —
    /// силы гаснут и кадр замирает; не живой — замерший кадр сразу (силы 0, время стоит). У запасных слоёв — часы идут при
    /// <c>live &amp;&amp; playing</c>.
    /// </summary>
    public void SetMotion(bool live, bool playing)
    {
        if (_live == live && _playing == playing) return;
        _live = live;
        _playing = playing;
        UpdateDrivers();
    }

    private void UpdateDrivers()
    {
        if (_fluid is null)
        {
            Running = _live && _playing;
            return;
        }
        Running = false;
        if (Audio is { } audio)
        {
            audio.SpectrumWanted = _live && _playing;
            if (_live) audio.EnsureStarted();
        }
        RequestFrame();
    }

    private void OnFrame()
    {
        var now = _stopwatch.Elapsed;
        _clock.Advance(now - _lastTick, running: true);
        _lastTick = now;
        foreach (var slot in _slots) Move(slot);
    }

    /// <summary>
    /// Показать обложку: наплывает поверх прежней за <see cref="BackdropMotion.CrossfadeMs"/> (<paramref name="animate"/> —
    /// нет, если в системе выключены анимации). Часы не сбрасываются.
    /// </summary>
    public async Task ShowAsync(string key, BackdropCover cover, bool animate)
    {
        if (key == Key) return;
        var version = ++_version;
        Key = key;
        _cover = cover;
        if (TryEnsureShader())
        {
            await ShowShaderAsync(version, cover, animate);
            return;
        }
        Ensure();
        if (_compositor is null || _root is null) return;

        var surface = await TextureAsync(cover.Tinted());
        if (version != _version)
        {
            // Пока текстура грузилась, показали другую обложку
            surface?.Dispose();
            return;
        }
        if (surface is null)
        {
            Key = null;
            return;
        }
        var slot = new Slot(_compositor, ToColor(cover.Base), surface, Brushes(surface));
        _root.Children.InsertBelow(slot.Root, _vignette);
        _slots.Add(slot);
        Layout(slot);

        if (!animate)
        {
            Cleanup(slot);
            return;
        }
        slot.Root.Opacity = 0;
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, _compositor.CreateLinearEasingFunction());
        fade.Duration = TimeSpan.FromMilliseconds(BackdropMotion.CrossfadeMs);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        slot.Root.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => Cleanup(slot));
    }

    // ---------- Шейдер (tasks/0035) ----------

    /// <summary>
    /// Поднять шейдер при первой обложке: панель, Direct3D, swap chain. Не вышло — навсегда (до перезапуска) остаётся фон из
    /// tasks/0034; причина — в журнале.
    /// </summary>
    private bool TryEnsureShader()
    {
        if (_fluid is not null) return true;
        if (_fluidFailed) return false;
        FluidRenderer? renderer = null;
        try
        {
            var panel = new SwapChainPanel { IsHitTestVisible = false };
            Children.Insert(0, panel);
            _panel = panel;
            renderer = FluidRenderer.TryCreate((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), pointer => SwapChainPanelInterop.SetSwapChain(panel, pointer));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Log.Warn("Fluid backdrop: panel failed", e);
        }
        if (renderer is null)
        {
            _fluidFailed = true;
            if (_panel is not null) Children.Remove(_panel);
            _panel = null;
            return false;
        }
        _fluid = renderer;
        _visualizer = new FluidVisualizer(Audio?.Spectrum ?? new SpectrumTimeline());
        UpdateDrivers();
        return true;
    }

    /// <summary>
    /// Новая обложка в шейдер. Если на экране уже была другая — прежняя уходит в подложку за 120 мс, потом новая проявляется за
    /// 280 мс (в шейдер вместо двух текстур одна, и наплыва «обложка в обложку» у него нет); без анимаций — сразу.
    /// </summary>
    private async Task ShowShaderAsync(int version, BackdropCover cover, bool animate)
    {
        if (_fluid is not { } renderer || _panel is not { } panel) return;
        if (cover.FluidPixels is not { } pixels)
        {
            Key = null;
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        var fade = animate && renderer.HasCover;
        if (fade)
        {
            Fade(visual, 0, 120);
            await Task.Delay(130);
            if (version != _version || _fluid is null) return;
        }
        if (!renderer.SetCover(pixels, cover.Luma, cover.Shares))
        {
            OnShaderFailed();
            return;
        }
        RequestFrame();
        if (fade || visual.Opacity < 1) Fade(visual, 1, 280);
        else visual.Opacity = 1;
    }

    private static void Fade(Visual visual, float to, int milliseconds)
    {
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, visual.Compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>Кадр шейдера нужен: подписка на часы кадров системы, пока есть что рисовать.</summary>
    private void RequestFrame()
    {
        if (_fluid is null || _subscribed) return;
        _subscribed = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// Кадр — по часам кадров системы (частота монитора), не по таймеру. Живой и играет — каждый кадр; живой и пауза — пока силы не
    /// погаснут; не живой — один замерший кадр (силы 0, время стоит). Дальше подписка снимается, последний кадр остаётся.
    /// </summary>
    private void OnRendering(object? sender, object e)
    {
        if (_fluid is not { } renderer || _visualizer is not { } visualizer)
        {
            Unsubscribe();
            return;
        }
        var now = _stopwatch.Elapsed;
        var dt = _lastFrame is { } last ? (now - last).TotalSeconds : 1.0 / 60;
        _lastFrame = now;
        int width = (int)Math.Ceiling(ActualWidth), height = (int)Math.Ceiling(ActualHeight);
        if (width < 1 || height < 1 || !renderer.HasCover)
        {
            // Ещё нечего рисовать: придёт SizeChanged или обложка
            Unsubscribe();
            return;
        }

        if (_live)
        {
            if (Audio is { } audio)
            {
                if (Position?.Invoke() is { } position) audio.SetPlayback(position, _playing, Volume?.Invoke() ?? 1);
                visualizer.Advance(dt, _playing, audio.CurrentPosition());
            }
            else visualizer.Advance(dt, _playing, null);
        }
        else visualizer.Freeze();

        if (!renderer.Render(width, height, visualizer.Motion, visualizer.Analysis))
        {
            OnShaderFailed();
            return;
        }
        if (!_live || !_playing && visualizer.IsSettled) Unsubscribe();
    }

    /// <summary>Шейдер отказал (устройство потеряно и т. п.): панель долой, на экране снова фон из tasks/0034.</summary>
    private void OnShaderFailed()
    {
        Unsubscribe();
        _fluidFailed = true;
        _fluid?.Dispose();
        _fluid = null;
        _visualizer = null;
        if (_panel is not null) Children.Remove(_panel);
        _panel = null;
        if (Audio is { } audio) audio.SpectrumWanted = false;
        Log.Warn("Fluid backdrop failed, plain blurred cover used");
        if (_cover is { } cover && Key is { } key)
        {
            Key = null;
            _ = ShowAsync(key, cover, animate: false);
        }
        UpdateDrivers();
    }

    /// <summary>Новый слот закрыл всё, что под ним: старые слои больше не нужны.</summary>
    private void Cleanup(Slot top)
    {
        var index = _slots.IndexOf(top);
        if (index < 0) return;
        top.Root.Opacity = 1;
        for (var i = 0; i < index; i++)
        {
            _root?.Children.Remove(_slots[i].Root);
            _slots[i].Dispose();
        }
        _slots.RemoveRange(0, index);
    }

    private void Ensure()
    {
        if (_root is not null) return;
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _root = _compositor.CreateContainerVisual();
        _root.Clip = _compositor.CreateInsetClip();

        // Виньетка: радиальный градиент из середины, радиусы 0,75 · w и 0,75 · h; до 55 % прозрачный, на краю чёрный 0,30
        _vignetteBrush = _compositor.CreateRadialGradientBrush();
        _vignetteBrush.MappingMode = CompositionMappingMode.Absolute;
        var clear = Color.FromArgb(0, 0, 0, 0);
        _vignetteBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0, clear));
        _vignetteBrush.ColorStops.Add(_compositor.CreateColorGradientStop((float)BackdropMotion.VignetteStart, clear));
        _vignetteBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb((byte)Math.Round(255 * BackdropMotion.VignetteEdge), 0, 0, 0)));
        _vignette = _compositor.CreateSpriteVisual();
        _vignette.Brush = _vignetteBrush;
        _vignette.RelativeSizeAdjustment = Vector2.One;
        _root.Children.InsertAtTop(_vignette);
        ElementCompositionPreview.SetElementChildVisual(this, _root);
        Layout();
    }

    /// <summary>Размытие слоя: <c>GaussianBlurEffect</c> из Win2D; если Win2D не заводится, слои идут без размытия.</summary>
    private CompositionBrush Brushes(ICompositionSurface surface)
    {
        var texture = _compositor!.CreateSurfaceBrush(surface);
        texture.Stretch = CompositionStretch.Fill;
        texture.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        if (!_blurFailed)
        {
            try
            {
                _blur ??= _compositor.CreateEffectFactory(new GaussianBlurEffect
                {
                    Name = "Blur",
                    BlurAmount = BlurSigma,
                    BorderMode = EffectBorderMode.Soft,
                    Optimization = EffectOptimization.Speed,
                    Source = new CompositionEffectSourceParameter("Source"),
                });
                var brush = _blur.CreateBrush();
                brush.SetSourceParameter("Source", texture);
                return brush;
            }
            catch (Exception e)
            {
                _blurFailed = true;
                Log.Warn("Backdrop blur unavailable, plain texture used", e);
            }
        }
        return texture;
    }

    /// <summary>Текстура 24 × 24 из байтов RGBA: PNG в памяти, <c>LoadedImageSurface</c>.</summary>
    private static async Task<LoadedImageSurface?> TextureAsync(byte[] rgba)
    {
        try
        {
            var bgra = new byte[rgba.Length];
            for (var i = 0; i + 3 < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, BackdropCover.Size, BackdropCover.Size, 96, 96, bgra);
            await encoder.FlushAsync();
            stream.Seek(0);
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            var loaded = new TaskCompletionSource<bool>();
            surface.LoadCompleted += (_, e) => loaded.TrySetResult(e.Status == LoadedImageSourceLoadStatus.Success);
            return await loaded.Task ? surface : null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Backdrop texture failed", e);
            return null;
        }
    }

    private void Layout()
    {
        if (_root is null) return;
        _root.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        if (_vignetteBrush is not null)
        {
            _vignetteBrush.EllipseCenter = new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2);
            _vignetteBrush.EllipseRadius = new Vector2((float)(BackdropMotion.VignetteRadius * ActualWidth), (float)(BackdropMotion.VignetteRadius * ActualHeight));
        }
        foreach (var slot in _slots) Layout(slot);
    }

    /// <summary>Размеры и опорные точки слоёв (меняются только вместе с размером области), затем положение.</summary>
    private void Layout(Slot slot)
    {
        var (w, h) = (ActualWidth, ActualHeight);
        foreach (var (visual, layer) in new[] { (slot.First, BackdropMotion.First(_clock.Seconds, w, h)), (slot.Second, BackdropMotion.Second(_clock.Seconds, w, h)) })
        {
            var side = (float)layer.Side;
            visual.Size = new Vector2(side, side);
            visual.AnchorPoint = new Vector2(0.5f, 0.5f);
            visual.CenterPoint = new Vector3(side / 2, side / 2, 0);
            visual.Opacity = (float)layer.Opacity;
        }
        Move(slot);
    }

    /// <summary>Положение слоёв для текущего времени часов.</summary>
    private void Move(Slot slot)
    {
        var (w, h) = (ActualWidth, ActualHeight);
        var first = BackdropMotion.First(_clock.Seconds, w, h);
        slot.First.Offset = new Vector3((float)first.CenterX, (float)first.CenterY, 0);
        slot.First.RotationAngleInDegrees = (float)first.Degrees;
        var second = BackdropMotion.Second(_clock.Seconds, w, h);
        slot.Second.Offset = new Vector3((float)second.CenterX, (float)second.CenterY, 0);
        slot.Second.RotationAngleInDegrees = (float)second.Degrees;
    }

    private static Color ToColor(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Одна обложка: подложка и два слоя одной текстуры; наплывает целиком.</summary>
    private sealed class Slot
    {
        private readonly LoadedImageSurface _surface;
        private readonly CompositionBrush _brush;

        public Slot(Compositor compositor, Color backdrop, LoadedImageSurface surface, CompositionBrush brush)
        {
            _surface = surface;
            _brush = brush;
            Root = compositor.CreateContainerVisual();
            Root.RelativeSizeAdjustment = Vector2.One;
            var fill = compositor.CreateSpriteVisual();
            fill.Brush = compositor.CreateColorBrush(backdrop);
            fill.RelativeSizeAdjustment = Vector2.One;
            Root.Children.InsertAtTop(fill);
            First = compositor.CreateSpriteVisual();
            First.Brush = brush;
            Root.Children.InsertAtTop(First);
            Second = compositor.CreateSpriteVisual();
            Second.Brush = brush;
            Root.Children.InsertAtTop(Second);
        }

        public ContainerVisual Root { get; }

        public SpriteVisual First { get; }

        public SpriteVisual Second { get; }

        public void Dispose()
        {
            Root.Dispose();
            _brush.Dispose();
            _surface.Dispose();
        }
    }
}
