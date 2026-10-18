namespace Melogold.Core.Visualizer;

/// <summary>
/// Очередь измерений спектра по времени звука (tasks/0035 §2 п. 5). Звук анализируется чуть раньше, чем звучит; рисуется то
/// измерение, которое <b>сейчас звучит</b> (по позиции плеера), а не пришедшее последним, иначе картинка опережает звук на
/// задержку вывода. Наполняет поток захвата, забирает поток кадров, поэтому всё под замком.
/// </summary>
public sealed class SpectrumTimeline
{
    /// <summary>Очередь длиннее этого — старое выбрасывается (никто не забирает: фон не на экране).</summary>
    private const int MaxFrames = 512;

    /// <summary>Позиция ушла назад больше чем на столько — перемотка: очередь устарела, с.</summary>
    private const double RewindSeconds = 0.5;

    private readonly object _gate = new();
    private readonly Queue<SpectrumFrame> _frames = new();
    private double _lastTime = double.NegativeInfinity;

    /// <summary>Добавить измерение (времена приходят по возрастанию).</summary>
    public void Push(SpectrumFrame frame)
    {
        lock (_gate)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > MaxFrames) _frames.Dequeue();
        }
    }

    /// <summary>
    /// Забрать измерения, время которых уже наступило (<c>время ≤ позиция</c>), по порядку. Позиция ушла назад (перемотка)
    /// — очередь с прежнего места сбрасывается.
    /// </summary>
    public void TakeDue(double positionSeconds, List<SpectrumFrame> into)
    {
        lock (_gate)
        {
            if (positionSeconds < _lastTime - RewindSeconds)
            {
                _frames.Clear();
                _lastTime = double.NegativeInfinity;
            }
            while (_frames.Count > 0 && _frames.Peek().Time <= positionSeconds)
            {
                var frame = _frames.Dequeue();
                _lastTime = Math.Max(_lastTime, frame.Time);
                into.Add(frame);
            }
        }
    }

    /// <summary>Сколько измерений ждёт своего времени.</summary>
    public int Pending
    {
        get
        {
            lock (_gate) return _frames.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _lastTime = double.NegativeInfinity;
        }
    }
}
