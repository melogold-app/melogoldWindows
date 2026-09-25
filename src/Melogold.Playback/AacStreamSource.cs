using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Melogold.Playback;

/// <summary>
/// Звук трека для <see cref="MediaStreamSource"/>: фрагменты DASH читаются диапазонами (~10 с звука каждый), кадры AAC
/// отдаются плееру с заголовками ADTS. Перемотка — по индексу <c>sidx</c> до фрагмента и по кадрам внутри него.
/// Чтобы звук начинался быстрее, сначала читаются только 64 КБ: заголовки и первые секунды первого фрагмента; его
/// остаток и два следующих фрагмента догружаются, пока звук уже идёт.
/// </summary>
public sealed class AacStreamSource : IDisposable
{
    private const int HeadBytes = 64 * 1024;

    private readonly HttpRangeReader _reader;
    private readonly byte[] _head;
    private readonly Dictionary<int, Task<Loaded>> _fragments = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _life = new();
    private int _fragment;
    private int _sample;
    private Loaded? _current;

    /// <summary>Фрагмент: все байты (или начало и задача на остаток) и его кадры.</summary>
    private sealed class Loaded(byte[] data, List<Mp4Sample> samples, int ready, Task? rest)
    {
        public byte[] Data { get; } = data;
        public List<Mp4Sample> Samples { get; } = samples;
        public int Ready { get; set; } = ready;
        public Task? Rest { get; } = rest;

        public bool Has(Mp4Sample sample) => sample.Offset + sample.Size <= Ready;
    }

    private AacStreamSource(HttpRangeReader reader, byte[] head, Mp4Index index)
    {
        _reader = reader;
        _head = head;
        Index = index;
    }

    public Mp4Index Index { get; }

    public StreamInfo Info => _reader.Info;

    public TimeSpan Duration => Index.Duration;

    /// <summary>Чтение фрагмента не удалось даже с повторами: плееру — ошибка с классом.</summary>
    public event Action<Exception>? Failed;

    public static async Task<AacStreamSource> OpenAsync(HttpClient http, StreamInfo info, Func<CancellationToken, Task<StreamInfo>> refresh, CancellationToken ct)
    {
        var reader = new HttpRangeReader(http, info, refresh);
        var head = await reader.ReadAsync(0, HeadBytes, ct).ConfigureAwait(false);
        Mp4Index? index = null;
        while (index is null)
        {
            try
            {
                index = FragmentedMp4.ParseIndex(head);
            }
            catch (Mp4FormatException e) when (e.Message == "truncated" && head.Length < 4 * 1024 * 1024)
            {
                // Длинный индекс (часовые видео): дочитать
                var more = await reader.ReadAsync(head.Length, Math.Max(head.Length, 256 * 1024), ct).ConfigureAwait(false);
                if (more.Length == 0) throw new StreamException(StreamErrorKind.Extractor, "Unsupported stream: truncated index");
                head = [.. head, .. more];
            }
            catch (Mp4FormatException e)
            {
                throw new StreamException(StreamErrorKind.Extractor, "Unsupported stream: " + e.Message);
            }
        }
        var source = new AacStreamSource(reader, head, index);
        source.Prefetch(0);
        source.Prefetch(1);
        return source;
    }

    public MediaStreamSource CreateMediaStreamSource()
    {
        var track = Index.Track;
        var properties = AudioEncodingProperties.CreateAacAdts((uint)track.SampleRate, (uint)track.Channels, (uint)Math.Max(track.AverageBitrate, 32_000));
        var source = new MediaStreamSource(new AudioStreamDescriptor(properties))
        {
            CanSeek = true,
            Duration = Duration,
            BufferTime = TimeSpan.Zero,
        };
        source.Starting += OnStarting;
        source.SampleRequested += OnSampleRequested;
        return source;
    }

    private Task<Loaded> Fragment(int index)
    {
        lock (_lock)
        {
            if (_fragments.TryGetValue(index, out var existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
            var task = LoadAsync(index);
            _fragments[index] = task;
            foreach (var key in _fragments.Keys.Where(k => k < index - 2 || k > index + 3).ToList()) _fragments.Remove(key);
            return task;
        }
    }

    private void Prefetch(int index)
    {
        if (index < 0 || index >= Index.Fragments.Count) return;
        lock (_lock)
        {
            if (_fragments.ContainsKey(index)) return;
        }
        _ = Fragment(index);
    }

    private async Task<Loaded> LoadAsync(int index)
    {
        var fragment = Index.Fragments[index];
        var inHead = (int)Math.Clamp(_head.Length - fragment.Offset, 0, fragment.Size);
        try
        {
            if (inHead == fragment.Size)
            {
                var whole = _head.AsSpan((int)fragment.Offset, fragment.Size).ToArray();
                return new Loaded(whole, FragmentedMp4.ParseFragment(whole, Index.Track), whole.Length, null);
            }
            if (inHead > 0)
            {
                // Начало фрагмента уже пришло с заголовками: играть его, пока догружается остаток
                var data = new byte[fragment.Size];
                _head.AsSpan((int)fragment.Offset, inHead).CopyTo(data);
                var samples = FragmentedMp4.ParseFragment(data, inHead, Index.Track);
                if (samples.Count > 0)
                {
                    Loaded? loaded = null;
                    var rest = Task.Run(async () =>
                    {
                        var tail = await _reader.ReadAsync(fragment.Offset + inHead, fragment.Size - inHead, _life.Token).ConfigureAwait(false);
                        tail.CopyTo(data, inHead);
                        loaded!.Ready = inHead + tail.Length;
                    });
                    loaded = new Loaded(data, samples, inHead, rest);
                    return loaded;
                }
            }
            var bytes = await _reader.ReadAsync(fragment.Offset, fragment.Size, _life.Token).ConfigureAwait(false);
            return new Loaded(bytes, FragmentedMp4.ParseFragment(bytes, Index.Track), bytes.Length, null);
        }
        catch (Exception e) when (e is Mp4FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new StreamException(StreamErrorKind.Extractor, $"Broken fragment {index}: {e.Message}");
        }
    }

    private TimeSpan Time(long ticks) => TimeSpan.FromSeconds((double)ticks / Index.Track.Timescale);

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        var request = args.Request;
        if (request.StartPosition is not { } start)
        {
            request.SetActualStartPosition(_current is { } loaded && _sample < loaded.Samples.Count ? Time(loaded.Samples[_sample].TimeTicks) : TimeSpan.Zero);
            return;
        }
        var deferral = request.GetDeferral();
        _ = SeekAsync(start).ContinueWith(task =>
        {
            try
            {
                request.SetActualStartPosition(task.IsCompletedSuccessfully ? task.Result : start);
            }
            finally
            {
                deferral.Complete();
            }
        }, TaskScheduler.Default);
    }

    private async Task<TimeSpan> SeekAsync(TimeSpan position)
    {
        _fragment = Index.FragmentAt(position);
        _current = await Fragment(_fragment).ConfigureAwait(false);
        var target = (long)(position.TotalSeconds * Index.Track.Timescale);
        _sample = Math.Max(0, _current.Samples.FindIndex(s => s.TimeTicks + s.DurationTicks > target));
        Prefetch(_fragment + 1);
        return _current.Samples.Count > 0 ? Time(_current.Samples[_sample].TimeTicks) : position;
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        // Кадр уже в памяти — отдаём сразу, без отложенного ответа
        if (_current is { } loaded && _sample < loaded.Samples.Count && loaded.Has(loaded.Samples[_sample]))
        {
            request.Sample = Take(loaded);
            return;
        }
        var deferral = request.GetDeferral();
        _ = NextSampleAsync().ContinueWith(task =>
        {
            try
            {
                request.Sample = task.IsCompletedSuccessfully ? task.Result : null;
                if (task.Exception is { } error) Failed?.Invoke(error.GetBaseException());
            }
            finally
            {
                deferral.Complete();
            }
        }, TaskScheduler.Default);
    }

    private async Task<MediaStreamSample?> NextSampleAsync()
    {
        while (_current is null || _sample >= _current.Samples.Count)
        {
            if (_current is not null) _fragment++;
            if (_fragment >= Index.Fragments.Count) return null;
            _current = await Fragment(_fragment).ConfigureAwait(false);
            _sample = 0;
            Prefetch(_fragment + 1);
            Prefetch(_fragment + 2);
        }
        if (!_current.Has(_current.Samples[_sample]) && _current.Rest is { } rest) await rest.ConfigureAwait(false);
        return Take(_current);
    }

    private MediaStreamSample Take(Loaded loaded)
    {
        var sample = loaded.Samples[_sample++];
        var frame = new byte[sample.Size + 7];
        FragmentedMp4.WriteAdtsHeader(frame, Index.Track, sample.Size);
        Buffer.BlockCopy(loaded.Data, sample.Offset, frame, 7, sample.Size);
        var result = MediaStreamSample.CreateFromBuffer(frame.AsBuffer(), Time(sample.TimeTicks));
        result.Duration = Time(sample.DurationTicks);
        return result;
    }

    public void Dispose()
    {
        _life.Cancel();
        lock (_lock) _fragments.Clear();
    }
}
