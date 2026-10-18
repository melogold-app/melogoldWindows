using System.Diagnostics;
using System.Runtime.InteropServices;
using Melogold.Core.Visualizer;

namespace Melogold.App.Services;

/// <summary>
/// Уровни звука для столбиков «играет» (docs/PROMPT.md §4: низы, середина, верх — под реальный звук, как
/// <c>service/AudioLevels.kt</c> Android). Источник — захват звука своего процесса (process loopback, Windows 11 и
/// Windows Server 2022+): то, что Melogold отдаёт в систему, после громкости и «без звука». Где захвата нет —
/// <see cref="IsLive"/> = false, и столбики двигаются спокойно, не притворяясь звуком. Те же сэмплы кормят фон «Сейчас играет»
/// (tasks/0035): <see cref="SpectrumAnalyzer"/> считает 48 полос, каждое измерение метится временем звука по <see cref="Clock"/>
/// и ждёт в <see cref="Spectrum"/>, пока позиция плеера до него дойдёт.
/// </summary>
public sealed class AudioLevels : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private float _low, _mid, _high;
    private volatile float _volume = 1;
    private volatile bool _spectrumWanted;

    /// <summary>
    /// Задержка между захватом звука и звуком из динамика (буфер вывода Windows), с. Измерение метится временем, когда оно
    /// зазвучит, а не когда его сняли; значение — оценка для проводного вывода, проверяется на глаз.
    /// </summary>
    private const double OutputLatencySeconds = 0.03;

    /// <summary>Измерения спектра по времени звука (tasks/0035 §2): наполняет поток захвата, забирает кадр фона.</summary>
    public SpectrumTimeline Spectrum { get; } = new();

    /// <summary>Позиция плеера ↔ часы: их обновляет кадр фона (<see cref="SetPlayback"/>), читает поток захвата.</summary>
    public PlaybackClock Clock { get; } = new();

    /// <summary>
    /// Нужен ли спектр: фон «Сейчас играет» на экране и живой. Без него БПФ не считается, а накопленное не копится.
    /// </summary>
    public bool SpectrumWanted
    {
        get => _spectrumWanted;
        set => _spectrumWanted = value;
    }

    /// <summary>
    /// Кадр фона сообщает позицию плеера и громкость (0..1, 0 — «без звука»). Захват берёт звук после регулятора громкости,
    /// а спектр должен быть до него (tasks/0035 §2), поэтому сэмплы делятся на громкость (но не больше чем в 20 раз).
    /// </summary>
    /// <summary>Позиция плеера по <see cref="Clock"/> сейчас (сглаженная); null — кадр фона ещё не сообщал.</summary>
    public double? CurrentPosition() => Clock.Estimate(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    public void SetPlayback(double positionSeconds, bool playing, double volume)
    {
        Clock.Update(positionSeconds, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, playing);
        _volume = (float)Math.Clamp(volume, 0, 1);
    }

    /// <summary>Захват работает: столбики показывают настоящий звук.</summary>
    public bool IsLive { get; private set; }

    /// <summary>Уровни 0…1 со сглаживанием: низы (до 250 Гц), середина, верх (от 2 кГц).</summary>
    public (float Low, float Mid, float High) Levels
    {
        get
        {
            lock (_gate) return (_low, _mid, _high);
        }
    }

    /// <summary>Запуск захвата при первом показе столбиков; повторный вызов ничего не делает.</summary>
    public void EnsureStarted()
    {
        if (_thread is not null) return;
        // Process loopback — с Windows 10 20348 (Windows 11 и Server 2022)
        if (Environment.OSVersion.Version.Build < 20348) return;
        _running = true;
        _thread = new Thread(Capture) { IsBackground = true, Name = "Melogold audio levels", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Capture()
    {
        IAudioClient? client = null;
        try
        {
            client = Activate();
            var format = new WaveFormatEx
            {
                wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = Channels * 4,
                nAvgBytesPerSec = SampleRate * Channels * 4,
                cbSize = 0,
            };
            const uint loopback = 0x00020000, autoConvert = 0x80000000, srcDefaultQuality = 0x08000000;
            client.Initialize(0, loopback | autoConvert | srcDefaultQuality, 2_000_000, 0, ref format, IntPtr.Zero);
            var captureId = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref captureId, out var service);
            var capture = (IAudioCaptureClient)service;
            client.Start();
            IsLive = true;
            Log.Info("Audio levels: process loopback");

            var filters = new BandFilters(SampleRate);
            var analyzer = new SpectrumAnalyzer(SampleRate);
            var frames = new List<SpectrumFrame>();
            var mono = new float[SampleRate / 10];
            var buffer = new float[SampleRate / 10 * Channels];
            while (_running)
            {
                Thread.Sleep(15);
                double low = 0, mid = 0, high = 0;
                var count = 0;
                while (capture.GetNextPacketSize() is var size && size > 0)
                {
                    capture.GetBuffer(out var data, out var packetFrames, out var flags, out _, out var qpc);
                    const int silent = 0x2;
                    var wantSpectrum = _spectrumWanted;
                    if (wantSpectrum && mono.Length < packetFrames) mono = new float[packetFrames * 2];
                    if ((flags & silent) == 0 && packetFrames > 0)
                    {
                        if (buffer.Length < packetFrames * Channels) buffer = new float[packetFrames * Channels * 2];
                        Marshal.Copy(data, buffer, 0, packetFrames * Channels);
                        var gain = 1 / Math.Max(_volume, 0.05f);
                        for (var i = 0; i < packetFrames; i++)
                        {
                            var sample = (buffer[i * 2] + buffer[i * 2 + 1]) * 0.5f;
                            var (l, m, h) = filters.Next(sample);
                            low += l * l;
                            mid += m * m;
                            high += h * h;
                            if (wantSpectrum) mono[i] = sample * gain;
                        }
                    }
                    else if (wantSpectrum && packetFrames > 0) Array.Clear(mono, 0, packetFrames);
                    if (wantSpectrum && packetFrames > 0) PushSpectrum(analyzer, frames, mono.AsSpan(0, packetFrames), qpc);
                    count += packetFrames;
                    capture.ReleaseBuffer(packetFrames);
                }
                Publish(count == 0 ? 0 : Math.Sqrt(low / count), count == 0 ? 0 : Math.Sqrt(mid / count), count == 0 ? 0 : Math.Sqrt(high / count), count > 0);
            }
            client.Stop();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or TimeoutException or EntryPointNotFoundException or DllNotFoundException)
        {
            IsLive = false;
            Log.Warn("Audio levels unavailable, calm bars", e);
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>
    /// Сэмплы пакета — в анализатор. Время звука первого сэмпла: когда пакет снят (<paramref name="qpc"/>, 100 нс, часы
    /// <c>QueryPerformanceCounter</c>, те же у <c>Stopwatch</c>) → позиция плеера по <see cref="Clock"/> + задержка вывода.
    /// Позиция неизвестна (кадр фона ещё не сообщил) — пакет пропускается.
    /// </summary>
    private void PushSpectrum(SpectrumAnalyzer analyzer, List<SpectrumFrame> frames, ReadOnlySpan<float> mono, long qpc)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var wall = qpc > 0 ? qpc / 1e7 : now;
        // Не верим отметке, если она из другого времени
        if (wall > now + 0.05 || wall < now - 1) wall = now;
        if (Clock.Estimate(wall) is not { } position) return;
        frames.Clear();
        analyzer.Push(mono, position + OutputLatencySeconds, frames);
        foreach (var frame in frames) Spectrum.Push(frame);
    }

    /// <summary>RMS полосы → 0…1 по шкале −50…−8 дБ; вверх быстро, вниз плавно.</summary>
    private void Publish(double low, double mid, double high, bool hadAudio)
    {
        static float Level(double rms) => (float)Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) + 50) / 42, 0, 1);
        static float Smooth(float current, float target) => target > current ? current + (target - current) * 0.6f : current + (target - current) * 0.15f;
        lock (_gate)
        {
            // Без пакетов (пауза, тишина) — плавно к нулю
            _low = Smooth(_low, hadAudio ? Level(low) : 0);
            _mid = Smooth(_mid, hadAudio ? Level(mid) : 0);
            _high = Smooth(_high, hadAudio ? Level(high) : 0);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }

    /// <summary>Однополюсные фильтры: низы — ниже 250 Гц, верх — выше 2 кГц, середина — между ними.</summary>
    private sealed class BandFilters(int sampleRate)
    {
        private readonly float _a250 = Alpha(250, sampleRate);
        private readonly float _a2000 = Alpha(2000, sampleRate);
        private float _lp250, _lp2000;

        private static float Alpha(double cutoff, int rate)
        {
            var rc = 1 / (2 * Math.PI * cutoff);
            var dt = 1.0 / rate;
            return (float)(dt / (rc + dt));
        }

        public (float Low, float Mid, float High) Next(float x)
        {
            _lp250 += _a250 * (x - _lp250);
            _lp2000 += _a2000 * (x - _lp2000);
            return (_lp250, _lp2000 - _lp250, x - _lp2000);
        }
    }

    // ---------- Активация process loopback ----------

    private static IAudioClient Activate()
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(), pBlobData = blob }, variant, false);
            var handler = new Completion();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Process loopback activation timed out");
            handler.Operation!.GetActivateResult(out var hr, out var activated);
            Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>Обработчик завершения активации; CCW .NET — агильный, как требует ActivateAudioInterfaceAsync.</summary>
    [ComVisible(true)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEventSlim Done { get; } = new();

        public IActivateAudioInterfaceAsyncOperation? Operation { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Operation = activateOperation;
            Done.Set();
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);
        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out int framesToRead, out int flags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int framesRead);
        int GetNextPacketSize();
    }
}
