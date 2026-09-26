using System.Buffers.Binary;
using System.Text;

namespace Melogold.Playback;

/// <summary>Что файл говорит о себе: теги iTunes (<c>moov/udta/meta/ilst</c>), которые читают все плееры.</summary>
public sealed record Mp4Tags(string? Title, string? Artist, string? Album, byte[]? Cover = null);

/// <summary>
/// «Сохранить файлом» (Android <c>FileExport</c> + <c>Mp4Tags</c>): фрагментированный MP4 (DASH), в котором YouTube отдаёт AAC,
/// превращается в обычный .m4a без перекодирования — те же кадры AAC одним <c>mdat</c>, <c>moov</c> в начале (файл играет
/// сразу, ещё не дочитанный), теги название, исполнитель, альбом и обложка. Android перекодирует, потому что у него
/// бывает Opus; у Windows поток всегда AAC, поэтому здесь без потерь.
/// </summary>
public static class Mp4Writer
{
    private static readonly uint[] Matrix = [0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000];

    /// <summary>Файл целиком (<paramref name="fragmented"/> — все байты потока) → .m4a с тегами.</summary>
    public static byte[] FromFragmented(byte[] fragmented, Mp4Tags tags)
    {
        var index = FragmentedMp4.ParseIndex(fragmented);
        var track = index.Track;
        var sizes = new List<int>();
        var durations = new List<uint>();
        var chunks = new List<(long Offset, int Samples)>();
        using var media = new MemoryStream();
        foreach (var fragment in index.Fragments)
        {
            if (fragment.Offset + fragment.Size > fragmented.Length) throw new Mp4FormatException("the stream is not complete");
            var data = fragmented.AsSpan((int)fragment.Offset, fragment.Size).ToArray();
            var samples = FragmentedMp4.ParseFragment(data, track);
            if (samples.Count == 0) continue;
            // Фрагмент — один кусок (chunk): его кадры лежат подряд
            chunks.Add((media.Position, samples.Count));
            foreach (var sample in samples)
            {
                media.Write(data, sample.Offset, sample.Size);
                sizes.Add(sample.Size);
                durations.Add((uint)sample.DurationTicks);
            }
        }
        if (sizes.Count == 0) throw new Mp4FormatException("no audio frames");

        var ftyp = Ftyp();
        // moov с условным началом mdat — только чтобы узнать его длину: смещения кусков в stco длины не меняют
        var probe = Moov(track, sizes, durations, chunks, 0, tags);
        var mediaStart = (long)ftyp.Length + probe.Length + 8;
        if (mediaStart + media.Length > uint.MaxValue) throw new Mp4FormatException("the file is too large");
        var moov = Moov(track, sizes, durations, chunks, mediaStart, tags);

        using var output = new MemoryStream((int)(mediaStart + media.Length));
        output.Write(ftyp);
        output.Write(moov);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(media.Length + 8));
        Encoding.ASCII.GetBytes("mdat", header[4..]);
        output.Write(header);
        media.Position = 0;
        media.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Ftyp()
    {
        var w = new BoxWriter();
        w.Begin("ftyp");
        w.Ascii("M4A ");
        w.U32(0x200);
        foreach (var brand in new[] { "M4A ", "mp42", "isom", "iso2" }) w.Ascii(brand);
        w.End();
        return w.ToArray();
    }

    private static byte[] Moov(Mp4AudioTrack track, List<int> sizes, List<uint> durations, List<(long Offset, int Samples)> chunks, long mediaStart, Mp4Tags tags)
    {
        var duration = durations.Sum(d => (long)d);
        var w = new BoxWriter();
        w.Begin("moov");

        w.BeginFull("mvhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32(track.Timescale);
        w.U32((uint)Math.Min(duration, uint.MaxValue));
        w.U32(0x00010000);
        w.U16(0x0100);
        w.Zeros(10);
        foreach (var value in Matrix) w.U32(value);
        w.Zeros(24);
        w.U32(2);
        w.End();

        w.Begin("trak");
        w.BeginFull("tkhd", 0, 0x000003);
        w.U32(0);
        w.U32(0);
        w.U32(1);
        w.U32(0);
        w.U32((uint)Math.Min(duration, uint.MaxValue));
        w.Zeros(8);
        w.U16(0);
        w.U16(0);
        w.U16(0x0100);
        w.U16(0);
        foreach (var value in Matrix) w.U32(value);
        w.U32(0);
        w.U32(0);
        w.End();

        w.Begin("mdia");
        w.BeginFull("mdhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32(track.Timescale);
        w.U32((uint)Math.Min(duration, uint.MaxValue));
        w.U16(0x55C4); // «und»
        w.U16(0);
        w.End();
        w.BeginFull("hdlr", 0, 0);
        w.U32(0);
        w.Ascii("soun");
        w.Zeros(12);
        w.Bytes(Encoding.ASCII.GetBytes("SoundHandler\0"));
        w.End();

        w.Begin("minf");
        w.BeginFull("smhd", 0, 0);
        w.U32(0);
        w.End();
        w.Begin("dinf");
        w.BeginFull("dref", 0, 0);
        w.U32(1);
        w.BeginFull("url ", 0, 1);
        w.End();
        w.End();
        w.End();

        w.Begin("stbl");
        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        w.Begin("mp4a");
        w.Zeros(6);
        w.U16(1);
        w.Zeros(8);
        w.U16((ushort)Math.Clamp(track.Channels, 1, 8));
        w.U16(16);
        w.U16(0);
        w.U16(0);
        w.U32((uint)Math.Min(track.SampleRate, 0xFFFF) << 16);
        Esds(w, track);
        w.End();
        w.End();

        // stts: одинаковые длительности подряд — одной записью
        var runs = new List<(uint Count, uint Delta)>();
        foreach (var delta in durations)
        {
            if (runs.Count > 0 && runs[^1].Delta == delta) runs[^1] = (runs[^1].Count + 1, delta);
            else runs.Add((1, delta));
        }
        w.BeginFull("stts", 0, 0);
        w.U32((uint)runs.Count);
        foreach (var (count, delta) in runs)
        {
            w.U32(count);
            w.U32(delta);
        }
        w.End();

        // stsc: сколько кадров в куске — меняется только на последнем фрагменте
        w.BeginFull("stsc", 0, 0);
        var stsc = new List<(uint First, uint Samples)>();
        for (var i = 0; i < chunks.Count; i++)
            if (stsc.Count == 0 || stsc[^1].Samples != chunks[i].Samples) stsc.Add(((uint)i + 1, (uint)chunks[i].Samples));
        w.U32((uint)stsc.Count);
        foreach (var (first, samples) in stsc)
        {
            w.U32(first);
            w.U32(samples);
            w.U32(1);
        }
        w.End();

        w.BeginFull("stsz", 0, 0);
        w.U32(0);
        w.U32((uint)sizes.Count);
        foreach (var size in sizes) w.U32((uint)size);
        w.End();

        w.BeginFull("stco", 0, 0);
        w.U32((uint)chunks.Count);
        foreach (var (offset, _) in chunks) w.U32((uint)(mediaStart + offset));
        w.End();

        w.End(); // stbl
        w.End(); // minf
        w.End(); // mdia
        w.End(); // trak

        Udta(w, tags);
        w.End(); // moov
        return w.ToArray();
    }

    /// <summary><c>esds</c>: ES → DecoderConfig (AAC, звук) → DecoderSpecificInfo (AudioSpecificConfig) → SLConfig.</summary>
    private static void Esds(BoxWriter w, Mp4AudioTrack track)
    {
        var asc = track.AudioSpecificConfig;
        var decoderConfigSize = 13 + 2 + asc.Length;
        var esSize = 3 + 2 + decoderConfigSize + 2 + 1;
        w.BeginFull("esds", 0, 0);
        w.U8(0x03);
        w.U8((byte)esSize);
        w.U16(1);
        w.U8(0);
        w.U8(0x04);
        w.U8((byte)decoderConfigSize);
        w.U8(0x40); // MPEG-4 Audio
        w.U8(0x15); // звук, поток вверх — 0
        w.U24(0);
        w.U32((uint)track.AverageBitrate);
        w.U32((uint)track.AverageBitrate);
        w.U8(0x05);
        w.U8((byte)asc.Length);
        w.Bytes(asc);
        w.U8(0x06);
        w.U8(1);
        w.U8(2);
        w.End();
    }

    /// <summary><c>udta/meta/ilst</c>: ©nam, ©ART, ©alb и covr — как их пишет iTunes и читает проводник Windows.</summary>
    private static void Udta(BoxWriter w, Mp4Tags tags)
    {
        var items = new List<(byte[] Type, uint Kind, byte[] Data)>();
        void Text(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) items.Add(([0xA9, .. Encoding.ASCII.GetBytes(name)], 1, Encoding.UTF8.GetBytes(value.Trim())));
        }
        Text("nam", tags.Title);
        Text("ART", tags.Artist);
        Text("alb", tags.Album);
        Text("too", "Melogold");
        if (tags.Cover is { Length: > 4 } cover)
            items.Add((Encoding.ASCII.GetBytes("covr"), cover[0] == 0x89 && cover[1] == 0x50 ? 14u : 13u, cover));
        if (items.Count == 0) return;

        w.Begin("udta");
        w.BeginFull("meta", 0, 0);
        w.BeginFull("hdlr", 0, 0);
        w.U32(0);
        w.Ascii("mdir");
        w.Ascii("appl");
        w.Zeros(9);
        w.End();
        w.Begin("ilst");
        foreach (var (type, kind, data) in items)
        {
            w.Begin(type);
            w.Begin("data");
            w.U32(kind);
            w.U32(0);
            w.Bytes(data);
            w.End();
            w.End();
        }
        w.End();
        w.End();
        w.End();
    }

    /// <summary>Боксы MP4 по порядку: размер пишется, когда бокс закрыт.</summary>
    private sealed class BoxWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly Stack<long> _open = new();

        public void Begin(string type) => Begin(Encoding.ASCII.GetBytes(type));

        public void Begin(byte[] type)
        {
            _open.Push(_stream.Position);
            U32(0);
            Bytes(type);
        }

        public void BeginFull(string type, byte version, uint flags)
        {
            Begin(type);
            U8(version);
            U24(flags);
        }

        public void End()
        {
            var start = _open.Pop();
            var size = _stream.Position - start;
            var position = _stream.Position;
            _stream.Position = start;
            U32((uint)size);
            _stream.Position = position;
        }

        public void U8(byte value) => _stream.WriteByte(value);

        public void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            _stream.Write(b);
        }

        public void U24(uint value)
        {
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _stream.Write(b);
        }

        public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

        public void Bytes(ReadOnlySpan<byte> data) => _stream.Write(data);

        public void Zeros(int count)
        {
            for (var i = 0; i < count; i++) _stream.WriteByte(0);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
