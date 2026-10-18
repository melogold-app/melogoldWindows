namespace Melogold.Core.Visualizer;

/// <summary>
/// Движение фона, вариант 1 «капли — бас, течение — верха» (tasks/0035 §4; Linux — <c>Shared::advance</c> и <c>PRESETS[0]</c>
/// лаборатории <c>final_lab.rs</c>). Силы сглаживаются (вверх 5/с, вниз 1,8/с), время течения и капель идёт, только пока играет.
/// Постоянные шейдера — в <see cref="FluidLook"/>.
/// </summary>
public sealed class FluidMotion
{
    public const int Blobs = 6;

    /// <summary>Сглаживание сил, 1/с.</summary>
    private const float Rise = 5, Fall = 1.8f;

    /// <summary>Предел силы, к которой идёт сглаживание.</summary>
    private const float ForceMax = 1.5f, PulseMax = 1.2f;

    private const float LiquidGain = 1.0f, LavaGain = 1.0f;
    private const float PulseBeat = 1.0f, PulseBass = 0.4f;
    private const float LiquidSpeed = 1.0f, LavaSpeed = 1.0f;

    /// <summary>Течение: сила от верхов.</summary>
    public float Liquid { get; private set; }

    /// <summary>Капли: сила от баса.</summary>
    public float Lava { get; private set; }

    /// <summary>Пульс на удар (0..1,2; шейдер обрезает до 1).</summary>
    public float Pulse { get; private set; }

    /// <summary>Сила каждой из шести капель (все идут к <see cref="Lava"/>).</summary>
    public float[] Blob { get; } = new float[Blobs];

    /// <summary>Время течения (<c>uLiquidFlow</c>).</summary>
    public float LiquidFlow { get; private set; }

    /// <summary>Время капель (<c>uLavaFlow</c>).</summary>
    public float LavaFlow { get; private set; }

    /// <summary>Кадр: <paramref name="dt"/> — секунды с прошлого кадра, <paramref name="playing"/> — играет ли трек.</summary>
    public void Step(float dt, bool playing, FluidAnalysis analysis)
    {
        Liquid = Follow(Liquid, Math.Min(analysis.High * LiquidGain, ForceMax), dt);
        Lava = Follow(Lava, Math.Min(analysis.Bass * LavaGain, ForceMax), dt);
        for (var i = 0; i < Blobs; i++) Blob[i] = Follow(Blob[i], Math.Min(Lava, ForceMax), dt);

        // Пульс: удар — сразу, отпускает со своей скоростью сглаживания
        var target = Math.Min(analysis.Beat * PulseBeat + analysis.Bass * PulseBass, PulseMax);
        Pulse = target > Pulse ? target : Follow(Pulse, target, dt);

        if (playing)
        {
            LiquidFlow += dt * LiquidSpeed * (0.25f + 1.4f * Liquid);
            LavaFlow += dt * LavaSpeed * (0.2f + 1.2f * Lava);
        }
    }

    /// <summary>Силы в ноль, время стоит: замерший кадр.</summary>
    public void Freeze()
    {
        Liquid = Lava = Pulse = 0;
        Array.Clear(Blob);
    }

    /// <summary>Силы почти погасли (после паузы).</summary>
    public bool IsQuiet => Liquid < 0.004f && Lava < 0.004f && Pulse < 0.004f;

    private static float Follow(float current, float target, float dt)
    {
        var rate = target > current ? Rise : Fall;
        return current + (target - current) * Math.Min(rate * dt, 1);
    }
}

/// <summary>Постоянные шейдера фона для варианта 1 (tasks/0035 §4): они одни на всех платформах.</summary>
public static class FluidLook
{
    public const float LavaMix = 0.55f;
    public const float LiquidBase = 0.16f;
    public const float BlobScale = 1.0f;
    public const float Zoom = 0.02f;
    public const float LodBase = 5.3f;
    public const float LodPulse = 3.0f;
    public const float Bright = 1.0f;
    public const float Saturation = 1.6f;
}

/// <summary>
/// Всё, что нужно фону каждый кадр: очередь измерений → огибающие → движение. <see cref="Advance"/> забирает измерения, которые
/// сейчас звучат (по позиции плеера), и делает шаг; <see cref="Freeze"/> даёт замерший кадр.
/// </summary>
public sealed class FluidVisualizer(SpectrumTimeline timeline)
{
    /// <summary>Самое большое <c>dt</c>: после остановки кадров движение не прыгает, с.</summary>
    public const float MaxStep = 0.1f;

    private readonly List<SpectrumFrame> _due = [];

    public FluidAnalysis Analysis { get; } = new();

    public FluidMotion Motion { get; } = new();

    /// <summary>
    /// Кадр. <paramref name="dt"/> — секунды с прошлого кадра (больше 0,1 с обрезается); <paramref name="position"/> — позиция
    /// плеера в секундах (<c>null</c> — неизвестна, измерения не забираются).
    /// </summary>
    public void Advance(double dt, bool playing, double? position)
    {
        var step = (float)Math.Clamp(dt, 0, MaxStep);
        _due.Clear();
        // На паузе измерения не нужны: тишина, а после паузы старое не должно прийти разом
        if (!playing) timeline.Clear();
        else if (position is { } now) timeline.TakeDue(now, _due);
        foreach (var frame in _due) Analysis.Feed(frame.Bands);
        Analysis.Step(step, playing);
        Motion.Step(step, playing, Analysis);
    }

    /// <summary>Замерший кадр: силы в ноль, время течения и капель стоит, очередь измерений не копится.</summary>
    public void Freeze()
    {
        Analysis.Silence();
        Motion.Freeze();
        timeline.Clear();
    }

    /// <summary>Пауза доиграла: всё погасло, кадры больше не нужны.</summary>
    public bool IsSettled => Analysis.IsQuiet && Motion.IsQuiet;
}
