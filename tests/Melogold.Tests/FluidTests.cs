using Melogold.Core.Artwork;
using Melogold.Core.Visualizer;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Живой фон «Сейчас играет» (tasks/0035): звук → 48 полос, очередь по времени звука, огибающие и удар, движение варианта 1,
/// текстура обложки с мипами. Числа — общие с Linux (<c>final_lab.rs</c>).
/// </summary>
public class FluidTests
{
    private const int Rate = 48_000;

    private static float[] Sine(double hz, int count, double amplitude = 0.5) =>
        Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    private static int BandOf(double hz)
    {
        for (var band = 0; band < SpectrumAnalyzer.Bands; band++)
        {
            if (hz >= SpectrumAnalyzer.EdgeHz(band) && hz < SpectrumAnalyzer.EdgeHz(band + 1)) return band;
        }
        throw new ArgumentOutOfRangeException(nameof(hz));
    }

    [Fact]
    public void Band_edges_run_from_30_hz_to_16_khz_logarithmically()
    {
        Assert.Equal(30, SpectrumAnalyzer.EdgeHz(0), 6);
        Assert.Equal(16000, SpectrumAnalyzer.EdgeHz(48), 6);
        var ratio = SpectrumAnalyzer.EdgeHz(1) / SpectrumAnalyzer.EdgeHz(0);
        for (var band = 1; band < SpectrumAnalyzer.Bands; band++)
            Assert.Equal(ratio, SpectrumAnalyzer.EdgeHz(band + 1) / SpectrumAnalyzer.EdgeHz(band), 9);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(250)]
    [InlineData(4000)]
    [InlineData(9000)]
    public void A_sine_lights_the_band_that_holds_its_frequency(double hz)
    {
        var bands = new SpectrumAnalyzer(Rate).Analyze(Sine(hz, SpectrumAnalyzer.Size));

        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.Equal(BandOf(hz), loudest);
        Assert.True(bands[loudest] > 0.3f, $"полоса {loudest} = {bands[loudest]}");
        // Далёкие полосы тише
        Assert.True(bands[loudest] > bands[(loudest + 24) % SpectrumAnalyzer.Bands] + 0.2f);
    }

    [Fact]
    public void Silence_gives_zero_in_every_band()
    {
        var bands = new SpectrumAnalyzer(Rate).Analyze(new float[SpectrumAnalyzer.Size]);

        Assert.All(bands, band => Assert.Equal(0f, band));
    }

    [Fact]
    public void Band_values_stay_between_0_and_1_even_for_full_scale_noise()
    {
        var random = new Random(7);
        var noise = Enumerable.Range(0, SpectrumAnalyzer.Size).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();

        var bands = new SpectrumAnalyzer(Rate).Analyze(noise);

        Assert.All(bands, band => Assert.InRange(band, 0f, 1f));
        Assert.True(bands.Max() > 0.5f);
    }

    [Fact]
    public void A_measurement_comes_every_512_samples_and_is_stamped_with_the_time_of_the_window_middle()
    {
        var analyzer = new SpectrumAnalyzer(Rate);
        var frames = new List<SpectrumFrame>();

        // 4096 сэмплов с времени звука 10 с: окон 1 + (4096 − 2048) / 512 = 5
        analyzer.Push(Sine(1000, 4096), 10.0, frames);

        Assert.Equal(5, frames.Count);
        Assert.Equal(10.0 + 1024.0 / Rate, frames[0].Time, 9);
        for (var i = 1; i < frames.Count; i++) Assert.Equal(512.0 / Rate, frames[i].Time - frames[i - 1].Time, 9);
    }

    [Fact]
    public void Windows_can_span_several_pushes()
    {
        var whole = new List<SpectrumFrame>();
        new SpectrumAnalyzer(Rate).Push(Sine(1000, 6144), 5.0, whole);
        var parts = new List<SpectrumFrame>();
        var analyzer = new SpectrumAnalyzer(Rate);
        var signal = Sine(1000, 6144);
        for (var start = 0; start < signal.Length; start += 480)
            analyzer.Push(signal.AsSpan(start, Math.Min(480, signal.Length - start)), 5.0 + start / (double)Rate, parts);

        Assert.Equal(whole.Count, parts.Count);
        for (var i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i].Time, parts[i].Time, 6);
            Assert.Equal(whole[i].Bands, parts[i].Bands);
        }
    }

    [Fact]
    public void A_jump_in_time_drops_what_was_accumulated()
    {
        var analyzer = new SpectrumAnalyzer(Rate);
        var frames = new List<SpectrumFrame>();
        analyzer.Push(Sine(1000, 1500), 1.0, frames);

        // Перемотка на 60 с: прежние 1500 сэмплов в окно не попадают, окно собирается заново
        analyzer.Push(Sine(1000, 1500), 60.0, frames);
        Assert.Empty(frames);
        analyzer.Push(Sine(1000, 1500), 60.0 + 1500.0 / Rate, frames);

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.True(f.Time > 59));
    }

    [Fact]
    public void The_timeline_gives_the_measurements_that_are_sounding_now_in_order()
    {
        var timeline = new SpectrumTimeline();
        foreach (var t in new[] { 1.0, 1.1, 1.2, 1.3 }) timeline.Push(new SpectrumFrame(t, new float[48]));
        var due = new List<SpectrumFrame>();

        timeline.TakeDue(1.15, due);
        Assert.Equal([1.0, 1.1], due.Select(f => f.Time));

        due.Clear();
        timeline.TakeDue(1.15, due);
        Assert.Empty(due);

        timeline.TakeDue(2.0, due);
        Assert.Equal([1.2, 1.3], due.Select(f => f.Time));
        Assert.Equal(0, timeline.Pending);
    }

    [Fact]
    public void A_rewind_clears_the_queue_that_belonged_to_the_old_place()
    {
        var timeline = new SpectrumTimeline();
        var due = new List<SpectrumFrame>();
        timeline.Push(new SpectrumFrame(100.0, new float[48]));
        timeline.TakeDue(100.5, due);
        timeline.Push(new SpectrumFrame(101.0, new float[48]));
        timeline.Push(new SpectrumFrame(101.1, new float[48]));
        due.Clear();

        // Перемотка на 10 с: измерения со 101-й секунды не звучат
        timeline.TakeDue(10.0, due);

        Assert.Empty(due);
        Assert.Equal(0, timeline.Pending);
    }

    private static float[] Bass(float level)
    {
        var bands = new float[48];
        for (var i = 0; i < 10; i++) bands[i] = level;
        return bands;
    }

    [Fact]
    public void A_step_in_the_bass_is_a_beat_that_fades_in_a_quarter_of_a_second()
    {
        var analysis = new FluidAnalysis();
        for (var i = 0; i < 30; i++)
        {
            analysis.Feed(Bass(0.1f));
            analysis.Step(1 / 60f, playing: true);
        }
        // Первый скачок от тишины мог быть ударом; ровный бас после него ударов не добавляет
        var steady = analysis.Beats;
        Assert.InRange(steady, 0, 1);
        for (var i = 0; i < 60; i++)
        {
            analysis.Feed(Bass(0.1f));
            analysis.Step(1 / 60f, playing: true);
        }
        Assert.Equal(steady, analysis.Beats);

        analysis.Feed(Bass(0.8f));
        Assert.Equal(1, analysis.Beat);
        Assert.Equal(steady + 1, analysis.Beats);

        for (var i = 0; i < 15; i++) analysis.Step(1 / 60f, playing: true);
        Assert.True(analysis.Beat < 0.4f, $"через 0,25 с удар = {analysis.Beat}");
    }

    [Fact]
    public void A_steady_bass_is_not_a_beat_and_beats_are_at_least_0_2_s_apart()
    {
        var analysis = new FluidAnalysis();
        for (var i = 0; i < 120; i++)
        {
            analysis.Feed(Bass(0.6f));
            analysis.Step(1 / 60f, playing: true);
        }
        // Первый скачок от нуля до 0,6 мог дать один удар, дальше ровный бас ударов не даёт
        Assert.InRange(analysis.Beats, 0, 1);

        var before = analysis.Beats;
        // Две ступеньки подряд через 0,1 с: второй удар раньше 0,2 с не засчитывается
        analysis.Feed(Bass(0.0f));
        analysis.Step(0.05f, playing: true);
        analysis.Feed(Bass(0.9f));
        analysis.Step(0.1f, playing: true);
        analysis.Feed(Bass(0.0f));
        analysis.Feed(Bass(0.9f));
        Assert.True(analysis.Beats - before <= 1);
    }

    [Fact]
    public void Bands_rise_fast_and_fall_slowly_and_fade_out_on_pause()
    {
        var analysis = new FluidAnalysis();
        analysis.Feed(Bass(1f));
        analysis.Step(1 / 60f, playing: true);
        var rise = analysis.Bands48[0];
        Assert.InRange(rise, 0.25f, 0.3f); // 18 · dt = 0,3

        for (var i = 0; i < 60; i++) analysis.Step(1 / 60f, playing: true);
        Assert.True(analysis.Bands48[0] > 0.99f);
        Assert.True(analysis.Bass > 0.9f);

        // Пауза: цели гаснут, и полосы вместе с ними
        for (var i = 0; i < 300; i++) analysis.Step(1 / 60f, playing: false);
        Assert.True(analysis.IsQuiet, $"бас {analysis.Bass}");
    }

    [Fact]
    public void Groups_are_means_of_their_bands()
    {
        var analysis = new FluidAnalysis();
        var bands = new float[48];
        for (var i = 30; i < 48; i++) bands[i] = 1;
        analysis.Feed(bands);
        for (var i = 0; i < 300; i++) analysis.Step(1 / 60f, playing: true);

        Assert.Equal(1f, analysis.High, 0.01f);
        Assert.Equal(0f, analysis.Bass, 0.01f);
        Assert.Equal(0f, analysis.Mid, 0.01f);
        Assert.Equal(18f / 48f, analysis.Loud, 0.01f);
    }

    [Fact]
    public void The_pulse_jumps_with_a_beat_and_lets_go_softly()
    {
        var visualizer = new FluidVisualizer(new SpectrumTimeline());
        visualizer.Analysis.Feed(Bass(0.1f));
        visualizer.Analysis.Feed(Bass(0.9f));
        visualizer.Advance(1 / 60.0, playing: true, position: null);

        Assert.True(visualizer.Motion.Pulse > 0.9f);
        var first = visualizer.Motion.Pulse;
        for (var i = 0; i < 30; i++) visualizer.Advance(1 / 60.0, playing: true, position: null);
        Assert.True(visualizer.Motion.Pulse < first);
        Assert.True(visualizer.Motion.Pulse <= 1.2f);
    }

    [Fact]
    public void The_flow_runs_only_while_playing_and_faster_with_sound()
    {
        var calm = new FluidMotion();
        var analysis = new FluidAnalysis();
        calm.Step(1f, playing: true, analysis);
        Assert.Equal(0.25f, calm.LiquidFlow, 3);
        Assert.Equal(0.2f, calm.LavaFlow, 3);

        var stopped = calm.LiquidFlow;
        calm.Step(1f, playing: false, analysis);
        Assert.Equal(stopped, calm.LiquidFlow);

        var loud = new FluidMotion();
        var strong = new FluidAnalysis();
        var all = Enumerable.Repeat(1f, 48).ToArray();
        for (var i = 0; i < 120; i++)
        {
            strong.Feed(all);
            strong.Step(1 / 60f, playing: true);
            loud.Step(1 / 60f, playing: true, strong);
        }
        Assert.True(loud.LiquidFlow > 120 / 60f * 0.25f * 2, $"течение {loud.LiquidFlow}");
        Assert.True(loud.Liquid > 0.9f && loud.Lava > 0.9f);
        Assert.All(loud.Blob, blob => Assert.True(blob > 0.9f));
    }

    [Fact]
    public void Freeze_zeroes_the_forces_and_keeps_the_time_and_forgets_pending_measurements()
    {
        var timeline = new SpectrumTimeline();
        var visualizer = new FluidVisualizer(timeline);
        timeline.Push(new SpectrumFrame(0.0, Enumerable.Repeat(1f, 48).ToArray()));
        for (var i = 0; i < 60; i++) visualizer.Advance(1 / 60.0, playing: true, position: 1.0);
        var flow = visualizer.Motion.LiquidFlow;
        Assert.True(visualizer.Motion.Liquid > 0.5f);

        visualizer.Freeze();

        Assert.Equal(0, visualizer.Motion.Liquid);
        Assert.Equal(0, visualizer.Motion.Pulse);
        Assert.Equal(0, visualizer.Analysis.Loud);
        Assert.Equal(flow, visualizer.Motion.LiquidFlow);
        Assert.True(visualizer.IsSettled);
    }

    [Fact]
    public void A_measurement_is_used_only_when_its_time_has_come()
    {
        var timeline = new SpectrumTimeline();
        var visualizer = new FluidVisualizer(timeline);
        timeline.Push(new SpectrumFrame(5.0, Enumerable.Repeat(1f, 48).ToArray()));

        visualizer.Advance(1 / 60.0, playing: true, position: 4.9);
        Assert.Equal(0, visualizer.Analysis.Loud);

        visualizer.Advance(1 / 60.0, playing: true, position: 5.0);
        Assert.True(visualizer.Analysis.Loud > 0.01f);
    }

    [Fact]
    public void A_long_pause_between_frames_does_not_make_the_motion_jump()
    {
        var motion = new FluidVisualizer(new SpectrumTimeline());
        motion.Advance(30.0, playing: true, position: null);

        Assert.True(motion.Motion.LiquidFlow <= FluidVisualizer.MaxStep * 0.25f + 1e-6f);
    }

    [Fact]
    public void The_cover_texture_is_256_square_with_cell_averages()
    {
        // 512 × 512: левая половина красная, правая синяя
        var rgba = new byte[512 * 512 * 4];
        for (var y = 0; y < 512; y++)
        {
            for (var x = 0; x < 512; x++)
            {
                var i = (y * 512 + x) * 4;
                rgba[i] = (byte)(x < 256 ? 200 : 0);
                rgba[i + 2] = (byte)(x < 256 ? 0 : 100);
                rgba[i + 3] = 255;
            }
        }

        var texture = FluidTexture.Downscale(rgba, 512, 512, 2048);

        Assert.NotNull(texture);
        Assert.Equal(256 * 256 * 4, texture!.Length);
        Assert.Equal([200, 0, 0, 255], texture.AsSpan((10 * 256 + 5) * 4, 4).ToArray());
        Assert.Equal([0, 0, 100, 255], texture.AsSpan((10 * 256 + 250) * 4, 4).ToArray());
    }

    [Fact]
    public void A_small_cover_is_stretched_to_256()
    {
        var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };

        var texture = FluidTexture.Downscale(rgba, 2, 1, 8)!;

        Assert.Equal([255, 0, 0, 255], texture[..4]);
        Assert.Equal([0, 255, 0, 255], texture.AsSpan(255 * 4, 4).ToArray());
    }

    [Fact]
    public void Mips_halve_down_to_one_pixel_and_average_the_cover()
    {
        var level0 = new byte[256 * 256 * 4];
        for (var i = 0; i < level0.Length; i += 4)
        {
            var pixel = i / 4;
            var white = (pixel % 256 + pixel / 256) % 2 == 0;
            level0[i] = level0[i + 1] = level0[i + 2] = (byte)(white ? 255 : 0);
            level0[i + 3] = 255;
        }

        var chain = FluidTexture.Mips(level0);

        Assert.Equal(9, chain.Length);
        for (var level = 0; level < chain.Length; level++) Assert.Equal((256 >> level) * (256 >> level) * 4, chain[level].Length);
        // Шахматка 255/0: на каждом уровне, начиная с первого, серый (255 + 255 + 0 + 0 + 2) / 4 = 128
        Assert.All(chain[1].Where((_, i) => i % 4 != 3), v => Assert.Equal(128, v));
        Assert.Equal(128, chain[8][0]);
        Assert.Equal(255, chain[8][3]);
    }

    [Fact]
    public void The_prepared_cover_carries_the_fluid_texture()
    {
        var rgba = new byte[64 * 64 * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 30;
            rgba[i + 1] = 60;
            rgba[i + 2] = 90;
            rgba[i + 3] = 255;
        }

        var cover = BackdropCover.Prepare(rgba, 64, 64, 256)!;

        Assert.NotNull(cover.FluidPixels);
        Assert.Equal([30, 60, 90, 255], cover.FluidPixels![..4]);
        Assert.Equal((0.2126 * 30 + 0.7152 * 60 + 0.0722 * 90) / 255, cover.Luma, 6);
    }
}

/// <summary>Часы позиции плеера для привязки измерений к звуку (tasks/0035 §2 п. 5).</summary>
public class PlaybackClockTests
{
    [Fact]
    public void Nothing_is_known_before_the_first_sample()
    {
        Assert.Null(new PlaybackClock().Estimate(1.0));
    }

    [Fact]
    public void Steps_of_the_player_position_are_smoothed_into_a_steady_clock()
    {
        var clock = new PlaybackClock();
        // Плеер отдаёт позицию ступеньками по 100 мс, кадры идут через 1/60 с
        double worst = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            var wall = 100 + frame / 60.0;
            var truth = 5 + frame / 60.0;
            clock.Update(Math.Floor(truth * 10) / 10, wall, playing: true);
            if (frame > 120) worst = Math.Max(worst, Math.Abs(clock.Estimate(wall)! .Value - truth));
        }

        // Ступенька без сглаживания — до 0,1 с ошибки; с часами — заметно меньше
        Assert.True(worst < 0.07, $"ошибка {worst}");
    }

    [Fact]
    public void A_measurement_made_earlier_gets_an_earlier_track_time()
    {
        var clock = new PlaybackClock();
        clock.Update(30.0, 500.0, playing: true);

        Assert.Equal(29.98, clock.Estimate(499.98)!.Value, 6);
        Assert.Equal(30.0, clock.Estimate(500.0)!.Value, 6);
    }

    [Fact]
    public void A_seek_is_taken_at_once()
    {
        var clock = new PlaybackClock();
        for (var i = 0; i < 60; i++) clock.Update(10 + i / 60.0, 200 + i / 60.0, playing: true);

        clock.Update(120.0, 201.0, playing: true);

        Assert.Equal(120.0, clock.Estimate(201.0)!.Value, 6);
    }

    [Fact]
    public void On_pause_the_position_stands_and_after_resume_it_is_taken_at_once()
    {
        var clock = new PlaybackClock();
        clock.Update(10.0, 100.0, playing: true);
        clock.Update(10.5, 100.5, playing: false);

        Assert.Equal(10.5, clock.Estimate(160.0)!.Value, 6);

        clock.Update(10.5, 160.0, playing: true);
        Assert.Equal(10.5, clock.Estimate(160.0)!.Value, 6);
    }
}
