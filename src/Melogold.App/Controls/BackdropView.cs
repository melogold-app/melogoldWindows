using System.Diagnostics;
using System.Numerics;
using Melogold.App.Services;
using Melogold.Core.Artwork;
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

    public BackdropView()
    {
        IsHitTestVisible = false;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = FrameInterval;
        _timer.Tick += (_, _) => OnFrame();
        SizeChanged += (_, _) => Layout();
    }

    /// <summary>Ключ обложки, которая сейчас на экране (или наплывает); null — фона ещё нет.</summary>
    public string? Key { get; private set; }

    /// <summary>Секунды часов анимации.</summary>
    public double Seconds => _clock.Seconds;

    private bool _running;

    /// <summary>Часы идут и фон движется. Остановка оставляет тот же кадр; запуск продолжает с того же места.</summary>
    public bool Running
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
