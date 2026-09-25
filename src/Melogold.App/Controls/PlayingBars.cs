using Melogold.App.Services;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Melogold.App.Controls;

/// <summary>
/// Три столбика «играет» (docs/PROMPT.md §4): низы, середина, верх — под настоящий звук из <see cref="AudioLevels"/>.
/// Без захвата звука — спокойное медленное движение, на паузе — неподвижны.
/// </summary>
public sealed partial class PlayingBars : Grid
{
    private static readonly List<WeakReference<PlayingBars>> Instances = [];
    private static DispatcherQueueTimer? _timer;
    private static readonly DateTime Started = DateTime.UtcNow;

    private readonly Rectangle[] _bars = new Rectangle[3];

    public PlayingBars()
    {
        ColumnSpacing = 2;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < 3; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _bars[i] = new Rectangle { Fill = new SolidColorBrush(Colors.White), RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Bottom, Height = 3 };
            SetColumn(_bars[i], i);
            Children.Add(_bars[i]);
        }
        Loaded += (_, _) =>
        {
            App.Services.GetRequiredService<AudioLevels>().EnsureStarted();
            lock (Instances) Instances.Add(new WeakReference<PlayingBars>(this));
            EnsureTimer(DispatcherQueue);
        };
        Unloaded += (_, _) =>
        {
            lock (Instances) Instances.RemoveAll(w => !w.TryGetTarget(out var bars) || bars == this);
        };
    }

    public Brush BarBrush
    {
        set
        {
            foreach (var bar in _bars) bar.Fill = value;
        }
    }

    private static void EnsureTimer(DispatcherQueue queue)
    {
        if (_timer is not null) return;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => TickAll();
        _timer.Start();
    }

    private static void TickAll()
    {
        List<PlayingBars> alive;
        lock (Instances)
        {
            alive = Instances.Select(w => w.TryGetTarget(out var bars) ? bars : null).OfType<PlayingBars>().ToList();
            if (alive.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
                return;
            }
        }
        var levels = App.Services.GetRequiredService<AudioLevels>();
        var engine = App.Services.GetRequiredService<PlayerEngine>();
        var playing = engine.IsPlaying;
        var volume = engine.OutputVolume;
        float[] values;
        // Захват слышит звук после громкости: при заметной громкости уровни поправляются на неё, без звука — спокойно
        if (levels.IsLive && volume >= 0.02)
        {
            var (low, mid, high) = levels.Levels;
            var gain = (float)Math.Clamp(-20 * Math.Log10(volume) / 42, 0, 1);
            static float Lift(float level, float gain) => level > 0.01f ? level + gain : level;
            values = [Lift(low, gain), Lift(mid, gain), Lift(high, gain)];
        }
        else
        {
            // Спокойные столбики: медленно и неглубоко, без притворства звуком
            var t = (DateTime.UtcNow - Started).TotalSeconds;
            values = playing
                ? [(float)(0.45 + 0.2 * Math.Sin(t * 2.1)), (float)(0.55 + 0.2 * Math.Sin(t * 2.9 + 1)), (float)(0.4 + 0.2 * Math.Sin(t * 2.5 + 2))]
                : [0.3f, 0.5f, 0.35f];
        }
        foreach (var bars in alive) bars.Show(values);
    }

    private void Show(float[] values)
    {
        var height = ActualHeight;
        if (height <= 0) return;
        for (var i = 0; i < 3; i++) _bars[i].Height = Math.Max(2, height * (0.15 + 0.85 * Math.Clamp(values[i], 0, 1)));
    }
}
