namespace Melogold.Core.Visualizer;

/// <summary>
/// Часы позиции плеера для привязки измерений спектра к звуку (tasks/0035 §2 п. 5). Позиция, которую отдаёт плеер, меняется
/// ступеньками и читается раз в кадр; поток захвата звука метит измерения в своём времени. Часы сводят то и другое:
/// <c>позиция ≈ время + сдвиг</c>, сдвиг подтягивается к каждому отсчёту плеера понемногу (шум ступенек сглаживается), а скачок
/// больше 0,3 с — перемотка или возобновление после паузы — принимается сразу. Время — секунды любых монотонных часов
/// (<c>Stopwatch</c>), одних и тех же для <see cref="Update"/> и <see cref="Estimate"/>.
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>Скачок сдвига, после которого он принимается сразу, с.</summary>
    private const double JumpSeconds = 0.3;

    /// <summary>Доля отсчёта, с которой сдвиг подтягивается к нему.</summary>
    private const double Pull = 0.1;

    private readonly object _gate = new();
    private double _offset;
    private double _position;
    private bool _playing;
    private bool _known;

    /// <summary>Отсчёт плеера: позиция трека в <paramref name="wall"/> (секунды часов), играет ли.</summary>
    public void Update(double position, double wall, bool playing)
    {
        lock (_gate)
        {
            _position = position;
            if (!playing)
            {
                _playing = false;
                return;
            }
            var candidate = position - wall;
            if (!_playing || !_known || Math.Abs(candidate - _offset) > JumpSeconds) _offset = candidate;
            else _offset += (candidate - _offset) * Pull;
            _playing = true;
            _known = true;
        }
    }

    /// <summary>Позиция трека в момент <paramref name="wall"/>; null — отсчётов ещё не было. На паузе — позиция, где встали.</summary>
    public double? Estimate(double wall)
    {
        lock (_gate)
        {
            if (!_known) return null;
            return _playing ? wall + _offset : _position;
        }
    }
}
