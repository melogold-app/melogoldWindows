namespace Melogold.Core.Visualizer;

/// <summary>
/// Огибающие полос и удар по басу (tasks/0035 §3; Linux — <c>Analysis::feed/step</c> лаборатории <c>final_lab.rs</c>). Измерения
/// приходят в <see cref="Feed"/> (по одному на каждое окно БПФ, в порядке звучания), каждый кадр вызывается
/// <see cref="Step"/> с <c>dt</c> — секундами с прошлого кадра.
/// </summary>
public sealed class FluidAnalysis
{
    public const int Bands = SpectrumAnalyzer.Bands;

    /// <summary>Скорость полос к новому значению: вверх и вниз, 1/с.</summary>
    private const float BandRise = 18, BandFall = 4;

    /// <summary>Скорость, с которой гаснут цели на паузе, 1/с.</summary>
    private const float PauseDecay = 3;

    /// <summary>Скорость групп баса, середины, верха и громкости к своему среднему, 1/с.</summary>
    private const float GroupRate = 12;

    private const float BeatDecay = 4;
    private const float FluxAverageKeep = 0.94f, FluxAverageNew = 0.06f;
    private const float BeatRatio = 2.2f, BeatOffset = 0.035f, BeatPause = 0.2f;

    private readonly float[] _targets = new float[Bands];
    private float _previousBass;
    private float _fluxAverage;
    private float _sinceBeat = 1;

    /// <summary>Сглаженные полосы 0..1.</summary>
    public float[] Bands48 { get; } = new float[Bands];

    /// <summary>Среднее полос 0–9.</summary>
    public float Bass { get; private set; }

    /// <summary>Среднее полос 10–29.</summary>
    public float Mid { get; private set; }

    /// <summary>Среднее полос 30–47.</summary>
    public float High { get; private set; }

    /// <summary>Среднее всех полос.</summary>
    public float Loud { get; private set; }

    /// <summary>Удар: 1 в момент удара, дальше затухает (<c>×max(0, 1 − 4·dt)</c> за кадр).</summary>
    public float Beat { get; private set; }

    /// <summary>Сколько ударов найдено с начала (для тестов и отладки).</summary>
    public long Beats { get; private set; }

    /// <summary>Цели полос (до сглаживания).</summary>
    public IReadOnlyList<float> Targets => _targets;

    /// <summary>Принять измерение: 48 значений 0..1. Считает удар по сырым целям (до сглаживания).</summary>
    public void Feed(ReadOnlySpan<float> bands)
    {
        if (bands.Length < Bands) return;
        bands[..Bands].CopyTo(_targets);

        var bass = Mean(_targets, 0, 10);
        var flux = Math.Max(0, bass - _previousBass);
        _previousBass = bass;
        _fluxAverage = _fluxAverage * FluxAverageKeep + flux * FluxAverageNew;
        if (flux > _fluxAverage * BeatRatio + BeatOffset && _sinceBeat > BeatPause)
        {
            Beat = 1;
            Beats++;
            _sinceBeat = 0;
        }
    }

    /// <summary>Кадр: полосы идут к целям (вверх 18/с, вниз 4/с), на паузе цели гаснут (<c>×max(0, 1 − 3·dt)</c>).</summary>
    public void Step(float dt, bool playing)
    {
        for (var i = 0; i < Bands; i++)
        {
            if (!playing) _targets[i] *= Math.Max(0, 1 - dt * PauseDecay);
            var speed = _targets[i] > Bands48[i] ? BandRise : BandFall;
            Bands48[i] += (_targets[i] - Bands48[i]) * Math.Min(speed * dt, 1);
        }
        Bass = Follow(Bass, Mean(Bands48, 0, 10), dt);
        Mid = Follow(Mid, Mean(Bands48, 10, 30), dt);
        High = Follow(High, Mean(Bands48, 30, Bands), dt);
        Loud = Follow(Loud, Mean(Bands48, 0, Bands), dt);
        Beat *= Math.Max(0, 1 - dt * BeatDecay);
        _sinceBeat += dt;
    }

    /// <summary>Всё в ноль: замерший кадр («Живой фон» выключен, анимации выключены).</summary>
    public void Silence()
    {
        Array.Clear(_targets);
        Array.Clear(Bands48);
        Bass = Mid = High = Loud = Beat = 0;
        _previousBass = 0;
        _fluxAverage = 0;
    }

    /// <summary>Остались ли заметные значения (после паузы гаснут за пару секунд).</summary>
    public bool IsQuiet => Bass < 0.004f && Mid < 0.004f && High < 0.004f && Loud < 0.004f && Beat < 0.004f;

    private static float Follow(float current, float target, float dt) => current + (target - current) * Math.Min(GroupRate * dt, 1);

    private static float Mean(float[] values, int from, int to)
    {
        float sum = 0;
        for (var i = from; i < to; i++) sum += values[i];
        return sum / (to - from);
    }
}
