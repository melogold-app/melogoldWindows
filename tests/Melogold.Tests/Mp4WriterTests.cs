using System.Buffers.Binary;
using System.Text;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>«Сохранить файлом» (tasks/0003): fMP4, как отдаёт YouTube, → обычный .m4a с теми же кадрами и тегами.</summary>
public class Mp4WriterTests
{
    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U32((uint)(body.Length + 8)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    private static byte[] Full(string type, uint flags, params byte[][] parts) => Box(type, [U32(flags), .. parts]);

    /// <summary>Кадр AAC: байты по номеру, чтобы в выходе было видно, какой кадр где.</summary>
    private static byte[] Frame(int number, int size) => Enumerable.Range(0, size).Select(i => (byte)(number * 7 + i)).ToArray();

    /// <summary>fMP4 с <paramref name="fragments"/> фрагментами по <paramref name="perFragment"/> кадров (последний — короче).</summary>
    private static (byte[] File, List<byte[]> Frames) Fragmented(int fragments, int perFragment)
    {
        byte[] asc = [0x12, 0x10]; // AAC LC, 44100, 2 канала
        var esds = Full("esds", 0,
            [0x03, (byte)(3 + 2 + 13 + 2 + asc.Length + 2 + 1), 0x00, 0x01, 0x00,
             0x04, (byte)(13 + 2 + asc.Length), 0x40, 0x15, 0, 0, 0, .. U32(128000), .. U32(128000),
             0x05, (byte)asc.Length, .. asc,
             0x06, 0x01, 0x02]);
        var mp4a = Box("mp4a", new byte[6], U16(1), new byte[8], U16(2), U16(16), U16(0), U16(0), U32(44100u << 16), esds);
        var stbl = Box("stbl", Full("stsd", 0, U32(1), mp4a));
        var trak = Box("trak", Box("mdia", Full("mdhd", 0, U32(0), U32(0), U32(44100), U32(0), U16(0x55C4), U16(0)), Box("minf", stbl)));
        var mvex = Box("mvex", Full("trex", 0, U32(1), U32(1), U32(1024), U32(0), U32(0)));
        var moov = Box("moov", trak, mvex);
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("dash"), U32(0));

        var frames = new List<byte[]>();
        var chunks = new List<byte[]>();
        var number = 0;
        for (var f = 0; f < fragments; f++)
        {
            var count = f == fragments - 1 ? Math.Max(1, perFragment / 2) : perFragment;
            var samples = Enumerable.Range(0, count).Select(i => Frame(++number, 20 + (number % 5))).ToList();
            frames.AddRange(samples);
            byte[] Trun(int dataOffset) => Full("trun", 0x000301, [.. U32((uint)count), .. U32((uint)dataOffset),
                .. samples.SelectMany(s => U32(1024).Concat(U32((uint)s.Length)))]);
            // Смещение данных — от начала moof: сначала moof с условным смещением, чтобы узнать его длину
            byte[] Moof(int dataOffset) => Box("moof", Full("mfhd", 0, U32((uint)f + 1)),
                Box("traf", Full("tfhd", 0, U32(1)), Full("tfdt", 0, U32((uint)(f * perFragment * 1024))), Trun(dataOffset)));
            var moofLength = Moof(0).Length;
            chunks.Add([.. Moof(moofLength + 8), .. Box("mdat", samples.SelectMany(s => s).ToArray())]);
        }
        var references = chunks.SelectMany(c => U32((uint)c.Length).Concat(U32(1024u * (uint)perFragment)).Concat(U32(0x90000000))).ToArray();
        var sidx = Full("sidx", 0, U32(1), U32(44100), U32(0), U32(0), U16(0), U16((ushort)chunks.Count), references);
        return ([.. ftyp, .. moov, .. sidx, .. chunks.SelectMany(c => c)], frames);
    }

    /// <summary>Боксы верхнего уровня и вложенные — по пути вида «moov/trak/mdia/minf/stbl/stsz».</summary>
    private static (int Content, int End) Find(byte[] d, string path)
    {
        int start = 0, end = d.Length;
        foreach (var name in path.Split('/'))
        {
            var found = false;
            var i = start;
            while (i + 8 <= end)
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
                var type = Encoding.Latin1.GetString(d, i + 4, 4);
                if (type == name)
                {
                    // meta — полный бокс: детей после версии и флагов
                    start = i + 8 + (type == "meta" ? 4 : 0);
                    end = i + size;
                    found = true;
                    break;
                }
                i += size;
            }
            Assert.True(found, $"no {name} in {path}");
        }
        return (start, end);
    }

    [Fact]
    public void FramesAreTheSameAndInOrder()
    {
        var (file, frames) = Fragmented(fragments: 3, perFragment: 4);
        var m4a = Mp4Writer.FromFragmented(file, new Mp4Tags("Бармалей", "Gorilla Glue", "Сингл"));

        Assert.Equal("ftyp", Encoding.ASCII.GetString(m4a, 4, 4));
        var stsz = Find(m4a, "moov/trak/mdia/minf/stbl/stsz");
        Assert.Equal((uint)frames.Count, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(stsz.Content + 8)));
        for (var i = 0; i < frames.Count; i++)
            Assert.Equal((uint)frames[i].Length, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(stsz.Content + 12 + i * 4)));

        // Куски — по фрагменту: смещение каждого указывает на его первый кадр
        var stco = Find(m4a, "moov/trak/mdia/minf/stbl/stco");
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(stco.Content + 4)));
        var firsts = new[] { 0, 4, 8 };
        for (var c = 0; c < 3; c++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(stco.Content + 8 + c * 4));
            Assert.Equal(frames[firsts[c]], m4a.AsSpan(offset, frames[firsts[c]].Length).ToArray());
        }

        // mdat — все кадры подряд, и больше ничего
        var mdat = Find(m4a, "mdat");
        Assert.Equal(frames.SelectMany(f => f).ToArray(), m4a[mdat.Content..mdat.End]);

        // Длительность — сумма кадров по 1024
        var mdhd = Find(m4a, "moov/trak/mdia/mdhd");
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(mdhd.Content + 12)));
        Assert.Equal((uint)(frames.Count * 1024), BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(mdhd.Content + 16)));

        // AudioSpecificConfig — тот же, в DecoderSpecificInfo (тег 5)
        var stsd = Find(m4a, "moov/trak/mdia/minf/stbl/stsd");
        Assert.True(m4a.AsSpan(stsd.Content, stsd.End - stsd.Content).IndexOf(new byte[] { 0x05, 0x02, 0x12, 0x10 }) >= 0);
    }

    [Fact]
    public void TagsAndCoverAreWritten()
    {
        var (file, _) = Fragmented(fragments: 2, perFragment: 3);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
        var m4a = Mp4Writer.FromFragmented(file, new Mp4Tags("Бармалей", "Gorilla Glue, Lil Nakur", "Сингл", jpeg));

        string Text(string item)
        {
            var data = Find(m4a, $"moov/udta/meta/ilst/{item}/data");
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(data.Content)));
            return Encoding.UTF8.GetString(m4a, data.Content + 8, data.End - data.Content - 8);
        }

        Assert.Equal("Бармалей", Text("©nam"));
        Assert.Equal("Gorilla Glue, Lil Nakur", Text("©ART"));
        Assert.Equal("Сингл", Text("©alb"));
        var covr = Find(m4a, "moov/udta/meta/ilst/covr/data");
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(covr.Content)));
        Assert.Equal(jpeg, m4a[(covr.Content + 8)..covr.End]);
    }

    [Fact]
    public void IncompleteStreamIsRejected()
    {
        var (file, _) = Fragmented(fragments: 3, perFragment: 4);
        Assert.Throws<Mp4FormatException>(() => Mp4Writer.FromFragmented(file[..(file.Length - 10)], new Mp4Tags(null, null, null)));
    }

    /// <summary>
    /// Настоящий поток YouTube (файл загрузки <c>…\downloads\&lt;id&gt;.140.data</c> в MELOGOLD_REMUX_SAMPLE) → .m4a рядом;
    /// без переменной — пропуск. Файл проверяется проводником и плеером Windows вручную.
    /// </summary>
    [Fact]
    public void RealStreamFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("MELOGOLD_REMUX_SAMPLE") is not { Length: > 0 } path || !File.Exists(path)) return;
        var m4a = Mp4Writer.FromFragmented(File.ReadAllBytes(path), new Mp4Tags("Never Gonna Give You Up", "Rick Astley", "Whenever You Need Somebody"));
        File.WriteAllBytes(Path.ChangeExtension(path, ".m4a"), m4a);
        Assert.True(m4a.Length > 1_000_000);
    }
}
