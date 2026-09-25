using System.Buffers.Binary;

namespace Melogold.Playback;

/// <summary>Звуковая дорожка из <c>moov</c>: шкала времени, AudioSpecificConfig, параметры по умолчанию.</summary>
public sealed record Mp4AudioTrack(
    uint Timescale,
    byte[] AudioSpecificConfig,
    int ObjectType,
    int SampleRate,
    int SamplingFrequencyIndex,
    int Channels,
    int AverageBitrate,
    uint DefaultSampleDuration,
    uint DefaultSampleSize,
    long DurationTicks);

/// <summary>Фрагмент из <c>sidx</c>: где лежит (<c>moof</c>+<c>mdat</c>) и какое время покрывает.</summary>
public sealed record Mp4Fragment(int Index, long Offset, int Size, long StartTicks, long DurationTicks);

/// <summary>Кадр AAC: где он в данных фрагмента, когда и сколько звучит (в тиках <see cref="Mp4AudioTrack.Timescale"/>).</summary>
public readonly record struct Mp4Sample(int Offset, int Size, long TimeTicks, long DurationTicks);

/// <summary>Начало файла: дорожка и таблица фрагментов.</summary>
public sealed record Mp4Index(Mp4AudioTrack Track, IReadOnlyList<Mp4Fragment> Fragments, uint IndexTimescale)
{
    public TimeSpan Duration => Fragments.Count == 0
        ? TimeSpan.FromSeconds((double)Track.DurationTicks / Track.Timescale)
        : TimeSpan.FromSeconds((double)(Fragments[^1].StartTicks + Fragments[^1].DurationTicks) / IndexTimescale);

    /// <summary>Фрагмент, в котором звучит <paramref name="position"/>.</summary>
    public int FragmentAt(TimeSpan position)
    {
        var ticks = (long)(position.TotalSeconds * IndexTimescale);
        for (var i = 0; i < Fragments.Count; i++)
        {
            if (ticks < Fragments[i].StartTicks + Fragments[i].DurationTicks) return i;
        }
        return Math.Max(0, Fragments.Count - 1);
    }
}

public sealed class Mp4FormatException(string message) : Exception(message);

/// <summary>
/// Разбор фрагментированного MP4 (DASH), в котором YouTube отдаёт AAC (itag 140/139). MediaFoundation такие файлы
/// открывает, но сэмплы из <c>moof</c> не читает — поэтому кадры достаём сами и отдаём в <c>MediaStreamSource</c>.
/// </summary>
public static class FragmentedMp4
{
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32BigEndian(s[o..]);

    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64BigEndian(s[o..]);

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16BigEndian(s[o..]);

    /// <summary>Боксы на отрезке [start, end): тип, начало содержимого, конец.</summary>
    private static IEnumerable<(string Type, int Start, int Content, int End)> Boxes(byte[] data, int start, int end)
    {
        var i = start;
        while (i + 8 <= end)
        {
            long size = U32(data, i);
            var type = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (i + 16 > end) yield break;
                size = (long)U64(data, i + 8);
                header = 16;
            }
            else if (size == 0) size = end - i;
            if (size < header) yield break;
            var boxEnd = (int)Math.Min(i + size, int.MaxValue);
            yield return (type, i, i + header, boxEnd);
            i = boxEnd;
        }
    }

    private static (string Type, int Start, int Content, int End)? Child(byte[] data, int start, int end, string type) =>
        Boxes(data, start, end).Where(b => b.Type == type).Select(b => ((string, int, int, int)?)b).FirstOrDefault();

    /// <summary>
    /// Разбирает начало файла (<c>ftyp</c>, <c>moov</c>, <c>sidx</c>). <paramref name="head"/> должен содержать их целиком;
    /// иначе — <see cref="Mp4FormatException"/> с просьбой прочитать больше.
    /// </summary>
    public static Mp4Index ParseIndex(byte[] head)
    {
        Mp4AudioTrack? track = null;
        Mp4Fragment[]? fragments = null;
        uint indexTimescale = 0;
        foreach (var box in Boxes(head, 0, head.Length))
        {
            if (box.End > head.Length) throw new Mp4FormatException("truncated");
            switch (box.Type)
            {
                case "moov":
                    track = ParseMoov(head, box.Content, box.End);
                    break;
                case "sidx":
                    (indexTimescale, fragments) = ParseSidx(head, box.Content, box.End);
                    break;
                case "moof":
                    goto done;
            }
            if (track is not null && fragments is not null) break;
        }
        done:
        if (track is null) throw new Mp4FormatException("no moov");
        if (fragments is null) throw new Mp4FormatException("no sidx");
        return new Mp4Index(track, fragments, indexTimescale);
    }

    private static Mp4AudioTrack ParseMoov(byte[] d, int start, int end)
    {
        uint timescale = 0, defaultDuration = 1024, defaultSize = 0;
        long durationTicks = 0;
        byte[]? asc = null;
        int channels = 2, sampleRate = 44100, bitrate = 128_000;
        foreach (var box in Boxes(d, start, end))
        {
            if (box.Type == "mvex" && Child(d, box.Content, box.End, "trex") is { } trex)
            {
                // trex: версия/флаги(4) trackID(4) description(4) duration(4) size(4)
                defaultDuration = U32(d, trex.Content + 12);
                defaultSize = U32(d, trex.Content + 16);
            }
            if (box.Type != "trak") continue;
            var mdia = Child(d, box.Content, box.End, "mdia") ?? throw new Mp4FormatException("no mdia");
            if (Child(d, mdia.Content, mdia.End, "mdhd") is { } mdhd)
            {
                var version = d[mdhd.Content];
                timescale = version == 1 ? U32(d, mdhd.Content + 20) : U32(d, mdhd.Content + 12);
                durationTicks = version == 1 ? (long)U64(d, mdhd.Content + 24) : U32(d, mdhd.Content + 16);
            }
            var minf = Child(d, mdia.Content, mdia.End, "minf") ?? throw new Mp4FormatException("no minf");
            var stbl = Child(d, minf.Content, minf.End, "stbl") ?? throw new Mp4FormatException("no stbl");
            var stsd = Child(d, stbl.Content, stbl.End, "stsd") ?? throw new Mp4FormatException("no stsd");
            // stsd: версия/флаги(4) число(4), затем записи
            foreach (var entry in Boxes(d, stsd.Content + 8, stsd.End))
            {
                if (entry.Type != "mp4a") continue;
                // SampleEntry: 6 резерв + 2 индекс; AudioSampleEntry: 8 резерв, channels(2), size(2), 4 резерв, rate 16.16(4)
                var p = entry.Content + 8;
                channels = U16(d, p + 8);
                sampleRate = (int)(U32(d, p + 16) >> 16);
                if (Child(d, p + 20, entry.End, "esds") is { } esds) (asc, bitrate) = ParseEsds(d, esds.Content + 4, esds.End, bitrate);
            }
        }
        if (asc is null || asc.Length < 2) throw new Mp4FormatException("no AudioSpecificConfig");
        var objectType = asc[0] >> 3;
        var frequencyIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
        var channelConfig = (asc[1] >> 3) & 0x0F;
        if (channelConfig > 0) channels = channelConfig;
        if (frequencyIndex < SampleRates.Length) sampleRate = SampleRates[frequencyIndex];
        return new Mp4AudioTrack(timescale == 0 ? (uint)sampleRate : timescale, asc, objectType, sampleRate, frequencyIndex, channels, bitrate,
            defaultDuration, defaultSize, durationTicks);
    }

    public static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary><c>esds</c>: дескрипторы ES → DecoderConfig → DecoderSpecificInfo (AudioSpecificConfig).</summary>
    private static (byte[]? Asc, int Bitrate) ParseEsds(byte[] d, int p, int end, int bitrate)
    {
        (int Tag, int Size, int Content) Descriptor(int at)
        {
            var tag = d[at];
            var size = 0;
            var i = at + 1;
            for (var n = 0; n < 4; n++)
            {
                var b = d[i++];
                size = (size << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return (tag, size, i);
        }

        var es = Descriptor(p);
        if (es.Tag != 0x03) return (null, bitrate);
        var q = es.Content + 2;
        var flags = d[q++];
        if ((flags & 0x80) != 0) q += 2;
        if ((flags & 0x40) != 0) q += 1 + d[q];
        if ((flags & 0x20) != 0) q += 2;
        var config = Descriptor(q);
        if (config.Tag != 0x04) return (null, bitrate);
        var average = (int)U32(d, config.Content + 9);
        if (average > 0) bitrate = average;
        var info = Descriptor(config.Content + 13);
        if (info.Tag != 0x05 || info.Content + info.Size > end) return (null, bitrate);
        return (d.AsSpan(info.Content, info.Size).ToArray(), bitrate);
    }

    private static (uint Timescale, Mp4Fragment[] Fragments) ParseSidx(byte[] d, int p, int end)
    {
        var version = d[p];
        var timescale = U32(d, p + 8);
        long earliest, firstOffset;
        int q;
        if (version == 0)
        {
            earliest = U32(d, p + 12);
            firstOffset = U32(d, p + 16);
            q = p + 20;
        }
        else
        {
            earliest = (long)U64(d, p + 12);
            firstOffset = (long)U64(d, p + 20);
            q = p + 28;
        }
        var count = U16(d, q + 2);
        q += 4;
        var fragments = new Mp4Fragment[count];
        var offset = end + firstOffset;
        var time = earliest;
        for (var i = 0; i < count; i++)
        {
            var size = (int)(U32(d, q) & 0x7FFFFFFF);
            var duration = U32(d, q + 4);
            fragments[i] = new Mp4Fragment(i, offset, size, time, duration);
            offset += size;
            time += duration;
            q += 12;
        }
        return (timescale, fragments);
    }

    /// <summary>
    /// Кадры фрагмента: <paramref name="data"/> — байты <c>moof</c>+<c>mdat</c>, смещения кадров — внутри них.
    /// </summary>
    public static List<Mp4Sample> ParseFragment(byte[] data, Mp4AudioTrack track) => ParseFragment(data, data.Length, track);

    /// <summary>
    /// То же для фрагмента, от которого пришло только начало (<paramref name="available"/> байт): <c>moof</c> должен быть
    /// целиком, кадры могут выходить за доступное — их размеры известны из <c>trun</c>.
    /// </summary>
    public static List<Mp4Sample> ParseFragment(byte[] data, int available, Mp4AudioTrack track)
    {
        var samples = new List<Mp4Sample>();
        foreach (var moof in Boxes(data, 0, available).Where(b => b.Type == "moof").Where(b => b.End <= available))
        {
            foreach (var traf in Boxes(data, moof.Content, moof.End).Where(b => b.Type == "traf"))
            {
                uint defaultDuration = track.DefaultSampleDuration, defaultSize = track.DefaultSampleSize;
                long baseOffset = moof.Start;
                long time = 0;
                foreach (var box in Boxes(data, traf.Content, traf.End))
                {
                    switch (box.Type)
                    {
                        case "tfhd":
                        {
                            var flags = U32(data, box.Content) & 0xFFFFFF;
                            var q = box.Content + 8;
                            if ((flags & 0x01) != 0)
                            {
                                baseOffset = (long)U64(data, q);
                                q += 8;
                            }
                            if ((flags & 0x02) != 0) q += 4;
                            if ((flags & 0x08) != 0)
                            {
                                defaultDuration = U32(data, q);
                                q += 4;
                            }
                            if ((flags & 0x10) != 0) defaultSize = U32(data, q);
                            break;
                        }
                        case "tfdt":
                            time = data[box.Content] == 1 ? (long)U64(data, box.Content + 4) : U32(data, box.Content + 4);
                            break;
                        case "trun":
                        {
                            var flags = U32(data, box.Content) & 0xFFFFFF;
                            var count = (int)U32(data, box.Content + 4);
                            var q = box.Content + 8;
                            var dataOffset = 0;
                            if ((flags & 0x01) != 0)
                            {
                                dataOffset = (int)U32(data, q);
                                q += 4;
                            }
                            if ((flags & 0x04) != 0) q += 4;
                            var position = (int)(baseOffset + dataOffset);
                            for (var i = 0; i < count; i++)
                            {
                                var duration = defaultDuration;
                                var size = defaultSize;
                                if ((flags & 0x100) != 0)
                                {
                                    duration = U32(data, q);
                                    q += 4;
                                }
                                if ((flags & 0x200) != 0)
                                {
                                    size = U32(data, q);
                                    q += 4;
                                }
                                if ((flags & 0x400) != 0) q += 4;
                                if ((flags & 0x800) != 0) q += 4;
                                if (position + size > data.Length) throw new Mp4FormatException("sample outside the fragment");
                                samples.Add(new Mp4Sample(position, (int)size, time, duration));
                                position += (int)size;
                                time += duration;
                            }
                            break;
                        }
                    }
                }
            }
        }
        return samples;
    }

    /// <summary>
    /// Заголовок ADTS (7 байт, без CRC) для кадра AAC длиной <paramref name="payload"/>. HE-AAC (5, 29) подаётся как LC
    /// с базовой частотой — декодер находит SBR сам.
    /// </summary>
    public static void WriteAdtsHeader(Span<byte> header, Mp4AudioTrack track, int payload)
    {
        var objectType = track.ObjectType is 5 or 29 ? 2 : Math.Clamp(track.ObjectType, 1, 4);
        var frequencyIndex = track.SamplingFrequencyIndex < 13 ? track.SamplingFrequencyIndex : Array.IndexOf(SampleRates, 44100);
        var channels = Math.Clamp(track.Channels, 1, 7);
        var length = payload + 7;
        header[0] = 0xFF;
        header[1] = 0xF1;
        header[2] = (byte)(((objectType - 1) << 6) | (frequencyIndex << 2) | (channels >> 2));
        header[3] = (byte)(((channels & 3) << 6) | (length >> 11));
        header[4] = (byte)((length >> 3) & 0xFF);
        header[5] = (byte)(((length & 7) << 5) | 0x1F);
        header[6] = 0xFC;
    }
}
