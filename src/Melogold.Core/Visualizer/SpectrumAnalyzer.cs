using System.Numerics;

namespace Melogold.Core.Visualizer;

/// <summary>Одно измерение спектра: 48 полос 0..1 и время звука (секунды трека) середины окна БПФ.</summary>
public sealed record SpectrumFrame(double Time, float[] Bands);

/// <summary>
/// Звук → 48 полос для фона «Сейчас играет» (tasks/0035 §2). Числа — общие с Linux, Android и Apple:
/// моно (среднее каналов), окно Хэмминга, БПФ <see cref="Size"/> = 2048 с шагом <see cref="Hop"/> = 512 сэмплов; мощность бина
/// <c>P = (re² + im²) / N²</c> (как у элемента GStreamer <c>spectrum</c>, по нему выбирали); 48 полос от 30 Гц до 16 кГц по
/// логарифму; мощность полосы — среднее линейной мощности бинов в ней (хотя бы один бин); значение полосы —
/// <c>clamp((10·log10(P) + 3·log2(f_низ / 30) + 70) / 55, 0, 1)</c>, наклон +3 дБ на октаву, чтобы верха не спали.
/// Каждое измерение помечено временем звука, чтобы рисовать то, что звучит сейчас (<see cref="SpectrumTimeline"/>).
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int Size = 2048;
    public const int Hop = 512;
    public const int Bands = 48;
    public const double LowHz = 30;
    public const double HighHz = 16000;

    /// <summary>
    /// Ниже этой мощности бин считается тишиной: у GStreamer <c>spectrum</c> порог −90 дБ. Цифровая тишина даёт все полосы 0, а не
    /// остаток наклона (+27 дБ у верхней полосы поднял бы её до 0,12).
    /// </summary>
    private const double SilenceFloor = 1e-9;

    /// <summary>Разрыв во времени (перемотка, пауза без пакетов), после которого накопленные сэмплы отбрасываются, с.</summary>
    private const double GapSeconds = 0.25;

    private static readonly float[] Window = MakeWindow();

    /// <summary>Границы полос, Гц: 49 значений от 30 до 16 000 по логарифму.</summary>
    private static readonly double[] LowEdges = MakeEdges();

    private readonly int _rate;
    private readonly int[] _from = new int[Bands];
    private readonly int[] _to = new int[Bands];
    private readonly float[] _tilt = new float[Bands];
    private readonly float[] _re = new float[Size];
    private readonly float[] _im = new float[Size];
    private readonly Fft _fft = new(Size);

    private float[] _buffer = new float[Size * 2];
    private int _count;
    /// <summary>Номер сэмпла (со старта потока) для <c>_buffer[0]</c>.</summary>
    private long _bufferStart;
    private long _anchorIndex;
    private double _anchorTime;
    private bool _anchored;

    public SpectrumAnalyzer(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _rate = sampleRate;
        // Бинов Size / 2; ширина бина — rate / Size (как rate / 2 / бинов у Linux)
        var bin = (double)sampleRate / Size;
        const int bins = Size / 2;
        for (var band = 0; band < Bands; band++)
        {
            var lo = LowEdges[band];
            var hi = LowEdges[band + 1];
            var from = Math.Min((int)(lo / bin), bins - 1);
            var to = Math.Min(Math.Max((int)(hi / bin), from + 1), bins);
            _from[band] = from;
            _to[band] = to;
            _tilt[band] = (float)(3 * Math.Log2(lo / LowHz));
        }
    }

    /// <summary>Частота дискретизации, Гц.</summary>
    public int SampleRate => _rate;

    /// <summary>Нижняя граница полосы <paramref name="band"/> (0..48; 48 — верх последней), Гц.</summary>
    public static double EdgeHz(int band) => LowEdges[band];

    /// <summary>
    /// Принять моно-сэмплы (<paramref name="time"/> — время звука первого из них, секунды трека). Готовые измерения (по одному на каждые
    /// <see cref="Hop"/> сэмплов, когда накоплено окно) добавляются в <paramref name="output"/>. Разрыв времени больше 0,25 с —
    /// перемотка: накопленное отбрасывается.
    /// </summary>
    public void Push(ReadOnlySpan<float> mono, double time, List<SpectrumFrame> output)
    {
        if (mono.IsEmpty) return;
        var next = _bufferStart + _count;
        if (_anchored)
        {
            var expected = _anchorTime + (next - _anchorIndex) / (double)_rate;
            if (Math.Abs(time - expected) > GapSeconds)
            {
                _count = 0;
                _bufferStart = next;
            }
        }
        _anchorIndex = next;
        _anchorTime = time;
        _anchored = true;

        if (_count + mono.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + mono.Length));
        mono.CopyTo(_buffer.AsSpan(_count));
        _count += mono.Length;

        while (_count >= Size)
        {
            var middle = _bufferStart + Size / 2;
            var at = _anchorTime + (middle - _anchorIndex) / (double)_rate;
            output.Add(new SpectrumFrame(at, Analyze(_buffer.AsSpan(0, Size))));
            Array.Copy(_buffer, Hop, _buffer, 0, _count - Hop);
            _count -= Hop;
            _bufferStart += Hop;
        }
    }

    /// <summary>Сбросить накопленное (смена трека).</summary>
    public void Reset()
    {
        _count = 0;
        _anchored = false;
    }

    /// <summary>48 полос по одному окну из <see cref="Size"/> сэмплов.</summary>
    public float[] Analyze(ReadOnlySpan<float> samples)
    {
        if (samples.Length < Size) throw new ArgumentException("Окну нужно 2048 сэмплов", nameof(samples));
        for (var i = 0; i < Size; i++)
        {
            _re[i] = samples[i] * Window[i];
            _im[i] = 0;
        }
        _fft.Forward(_re, _im);

        const double scale = 1.0 / ((double)Size * Size);
        var result = new float[Bands];
        for (var band = 0; band < Bands; band++)
        {
            double sum = 0;
            for (var bin = _from[band]; bin < _to[band]; bin++) sum += (double)_re[bin] * _re[bin] + (double)_im[bin] * _im[bin];
            var power = sum * scale / (_to[band] - _from[band]);
            result[band] = power <= SilenceFloor
                ? 0
                : (float)Math.Clamp((10 * Math.Log10(power) + _tilt[band] + 70) / 55, 0, 1);
        }
        return result;
    }

    private static float[] MakeWindow()
    {
        var window = new float[Size];
        for (var i = 0; i < Size; i++) window[i] = (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Size - 1)));
        return window;
    }

    private static double[] MakeEdges()
    {
        var edges = new double[Bands + 1];
        for (var band = 0; band <= Bands; band++) edges[band] = LowHz * Math.Pow(HighHz / LowHz, (double)band / Bands);
        return edges;
    }
}

/// <summary>БПФ по основанию 2 на месте, для массивов float.</summary>
internal sealed class Fft
{
    private readonly int _size;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly int[] _reverse;

    public Fft(int size)
    {
        if (!BitOperations.IsPow2(size)) throw new ArgumentException("Размер БПФ — степень двойки", nameof(size));
        _size = size;
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }
        _reverse = new int[size];
        var bits = BitOperations.Log2((uint)size);
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
    }

    public void Forward(float[] re, float[] im)
    {
        for (var i = 0; i < _size; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var length = 2; length <= _size; length <<= 1)
        {
            var half = length / 2;
            var step = _size / length;
            for (var start = 0; start < _size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step];
                    var s = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * c - im[b] * s;
                    var ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
