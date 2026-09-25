using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Melogold.Core.Lyrics;

/// <summary>
/// LRC и расширенный LRC (A2): <c>[мм:сс.xx]строка</c>, несколько меток у строки, <c>[offset:±мс]</c>, метки слов
/// <c>&lt;мм:сс.xx&gt;</c> и дуэты «walaoke» <c>M:</c>, <c>F:</c>, <c>D:</c> (<c>spec/lyrics.md</c>, Android <c>LrcFormat.kt</c>).
/// </summary>
public static partial class LrcFormat
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?]")]
    private static partial Regex LineTag();

    [GeneratedRegex(@"^\[([a-zA-Z#]+):(.*)]\s*$")]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\s*([MFD]):\s?")]
    private static partial Regex Walaoke();

    private const long LastLineMs = 5_000;

    /// <summary>Похоже на LRC: хотя бы одна строка начинается с метки времени.</summary>
    public static bool Matches(string text) => Lines(text).Any(l => LineTag().IsMatch(l.Trim()));

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private sealed record RawLine(long StartMs, string Text, List<SyncedWord>? Words, string? Agent);

    /// <summary>Разбор LRC; null, если ни одна строка не размечена временем.</summary>
    public static SyncedLyrics? Parse(string text)
    {
        long offsetMs = 0;
        string? agent = null;
        var raw = new List<RawLine>();
        foreach (var sourceLine in Lines(text))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0) continue;
            if (MetaTag().Match(line) is { Success: true } meta)
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase))
                    offsetMs = long.TryParse(meta.Groups[2].Value.Trim().TrimStart('+'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o) ? o : 0;
                continue;
            }
            // Все метки перед текстом
            var starts = new List<long>();
            while (LineTag().Match(line) is { Success: true } tag)
            {
                starts.Add(Millis(tag));
                line = line[tag.Length..];
            }
            if (starts.Count == 0) continue;
            if (Walaoke().Match(line) is { Success: true } duet)
            {
                agent = duet.Groups[1].Value;
                line = line[duet.Length..];
            }
            var words = ParseWords(line);
            var lineText = words is null || words.Count == 0 ? line.Trim() : string.Concat(words.Select(w => w.Text)).Trim();
            foreach (var start in starts)
                raw.Add(new RawLine(Math.Max(0, start - offsetMs), lineText, words is null ? null : ShiftWords(words, start, start - offsetMs), agent));
        }

        var sorted = raw.OrderBy(r => r.StartMs).ToList();
        if (sorted.All(r => string.IsNullOrWhiteSpace(r.Text))) return null;
        var agents = SyncedLyrics.AssignSides(sorted.Select(r => r.Agent).OfType<string>());
        var sides = agents.ToDictionary(a => a.Id, a => a.Side);
        var wordTimed = sorted.Any(r => r.Words is { Count: > 0 });

        var lines = new List<SyncedLine>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var line = sorted[i];
            // Пустая строка с меткой только отмечает конец предыдущей
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            long? next = i + 1 < sorted.Count ? sorted[i + 1].StartMs : null;
            var words = (line.Words ?? []).Where(w => w.Text.Length > 0).ToList();
            // Последняя метка без текста («…слово<00:13.20>») — конец строки
            long? explicitEnd = line.Words is { Count: > 0 } all && all[^1].Text.Length == 0 ? all[^1].StartMs : null;
            var end = explicitEnd is { } e && e > line.StartMs ? e : next is { } n && n > line.StartMs ? n : line.StartMs + LastLineMs;
            lines.Add(new SyncedLine
            {
                StartMs = line.StartMs,
                EndMs = end,
                Text = line.Text,
                Words = words.Select(w => w with { EndMs = w.EndMs > w.StartMs ? w.EndMs : end }).ToList(),
                Agent = line.Agent,
                Side = line.Agent is { } id && sides.TryGetValue(id, out var side) ? side : VocalSide.Start,
            });
        }
        return new SyncedLyrics(lines, wordTimed ? LyricsTiming.Word : LyricsTiming.Line, agents);
    }

    /// <summary>LRC: расширенный (с метками слов), если текст размечен по словам; дуэт из 2–3 исполнителей — walaoke.</summary>
    public static string Write(SyncedLyrics lyrics, bool enhanced = true)
    {
        var builder = new StringBuilder();
        var walaoke = lyrics.Agents.Count is >= 2 and <= 3;
        var prefixes = lyrics.Agents.Select((a, i) => (a.Id, Prefix: "MFD"[i])).Take(3).ToDictionary(p => p.Id, p => p.Prefix);
        string? lastAgent = null;
        for (var i = 0; i < lyrics.Lines.Count; i++)
        {
            var line = lyrics.Lines[i];
            builder.Append('[').Append(Timestamp(line.StartMs)).Append(']');
            if (walaoke && line.Agent is { } agent && agent != lastAgent && prefixes.TryGetValue(agent, out var prefix))
            {
                builder.Append(prefix).Append(": ");
                lastAgent = agent;
            }
            if (enhanced && line.Words.Count > 0)
            {
                foreach (var word in line.Words) builder.Append('<').Append(Timestamp(word.StartMs)).Append('>').Append(word.Text);
                builder.Append('<').Append(Timestamp(line.Words[^1].EndMs)).Append('>');
            }
            else builder.Append(line.Text);
            builder.Append('\n');
            // Пауза перед следующей строкой — закрыть эту пустой строкой
            if (i + 1 >= lyrics.Lines.Count || lyrics.Lines[i + 1].StartMs > line.EndMs) builder.Append('[').Append(Timestamp(line.EndMs)).Append("]\n");
        }
        return builder.ToString();
    }

    private static List<(long Start, string Text)>? ParseWords(string line)
    {
        var tags = WordTag().Matches(line);
        if (tags.Count == 0) return null;
        var list = new List<(long, string)>();
        for (var i = 0; i < tags.Count; i++)
        {
            var textStart = tags[i].Index + tags[i].Length;
            var textEnd = i + 1 < tags.Count ? tags[i + 1].Index : line.Length;
            list.Add((Millis(tags[i]), line[textStart..textEnd]));
        }
        return list;
    }

    // Слово кончается там, где начинается следующее; пустая метка в конце заканчивает последнее
    private static List<SyncedWord> ShiftWords(List<(long Start, string Text)> words, long tagBase, long lineStart)
    {
        var delta = lineStart - tagBase;
        return words.Select((w, i) =>
        {
            var end = i + 1 < words.Count ? words[i + 1].Start : w.Start;
            return new SyncedWord(Math.Max(0, w.Start + delta), Math.Max(0, end + delta), w.Text);
        }).ToList();
    }

    private static long Millis(Match match)
    {
        var minutes = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[3].Value;
        long millis = fraction.Length switch
        {
            0 => 0,
            1 => long.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => long.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => long.Parse(fraction[..3], CultureInfo.InvariantCulture),
        };
        return (minutes * 60 + seconds) * 1000 + millis;
    }

    /// <summary><c>мм:сс.xx</c>, или <c>мм:сс.xxx</c>, если сотые потеряли бы время.</summary>
    internal static string Timestamp(long ms)
    {
        var total = Math.Max(0, ms);
        var minutes = total / 60_000;
        var seconds = total / 1000 % 60;
        var millis = total % 1000;
        return millis % 10 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{millis / 10:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{millis:000}");
    }
}

/// <summary>
/// TTML, как пишут Apple Music и база AMLL TTML: строки <c>p</c> со словами <c>span</c>, исполнители <c>ttm:agent</c>,
/// подпевка <c>x-bg</c>, перевод и транскрипция, <c>xml:lang</c>. В нём Melogold хранит тексты и делится ими.
/// </summary>
public static class TtmlFormat
{
    private const string NsTtml = "http://www.w3.org/ns/ttml";
    private const string NsTtm = "http://www.w3.org/ns/ttml#metadata";
    private const string NsItunes = "http://music.apple.com/lyric-ttml-internal";
    private const string NsXml = "http://www.w3.org/XML/1998/namespace";

    public static bool Matches(string text)
    {
        var head = text.TrimStart();
        head = head[..Math.Min(512, head.Length)];
        return (head.StartsWith("<?xml", StringComparison.Ordinal) || head.StartsWith("<tt", StringComparison.Ordinal)) && head.Contains("<tt", StringComparison.Ordinal);
    }

    /// <summary>Разбор TTML; null, если это не TTML или в нём нет строк со временем.</summary>
    public static SyncedLyrics? Parse(string text)
    {
        XmlElement? root;
        try
        {
            // Без DTD и внешних сущностей: файлы текстов приходят откуда угодно
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            document.Load(reader);
            root = document.DocumentElement;
        }
        catch (XmlException)
        {
            return null;
        }
        if (root is null || root.LocalName != "tt") return null;

        var declared = Elements(root).Where(e => e.LocalName == "agent")
            .Select(a => (Id: Attr(a, NsXml, "id"), Name: Elements(a).FirstOrDefault(n => n.LocalName == "name")?.InnerText.Trim() is { Length: > 0 } name ? name : null))
            .Where(a => a.Id is not null).ToList();
        var parsed = Elements(root).Where(e => e.LocalName == "p").Select(ParseLine).OfType<SyncedLine>().OrderBy(l => l.StartMs).ToList();
        if (parsed.Count == 0) return null;

        var order = declared.Select(a => a.Id!).Concat(parsed.Select(l => l.Agent).OfType<string>());
        var names = declared.GroupBy(a => a.Id!).ToDictionary(g => g.Key, g => g.First().Name);
        var agents = SyncedLyrics.AssignSides(order).Select(a => a with { Name = names.GetValueOrDefault(a.Id) }).ToList();
        var sides = agents.ToDictionary(a => a.Id, a => a.Side);
        var timing = (Attr(root, NsItunes, "timing") ?? (root.GetAttribute("itunes:timing") is { Length: > 0 } t ? t : null)) switch
        {
            "Line" => LyricsTiming.Line,
            "Word" => LyricsTiming.Word,
            _ => parsed.Any(l => l.Words.Count > 0) ? LyricsTiming.Word : LyricsTiming.Line,
        };
        return new SyncedLyrics(
            parsed.Select(l => l with { Side = l.Agent is { } id && sides.TryGetValue(id, out var side) ? side : VocalSide.Start }).ToList(),
            timing, agents, Attr(root, NsXml, "lang"));
    }

    /// <summary>TTML, совместимый с Apple и AMLL.</summary>
    public static string Write(SyncedLyrics lyrics)
    {
        var builder = new StringBuilder();
        // Дуэт без объявленных исполнителей получает v1 (начало) и v2 (конец)
        IReadOnlyList<LyricsAgent> agents = lyrics.Agents.Count > 0 ? lyrics.Agents
            : lyrics.IsDuet ? [new LyricsAgent("v1", VocalSide.Start), new LyricsAgent("v2", VocalSide.End)] : [];
        var agentBySide = agents.GroupBy(a => a.Side).ToDictionary(g => g.Key, g => g.First().Id);
        var end = lyrics.Lines.Count > 0 ? lyrics.Lines.Max(l => l.EndMs) : 0;
        builder.Append($"<tt xmlns=\"{NsTtml}\" xmlns:ttm=\"{NsTtm}\" xmlns:itunes=\"{NsItunes}\"");
        builder.Append($" itunes:timing=\"{(lyrics.Timing == LyricsTiming.Word ? "Word" : "Line")}\"");
        if (lyrics.Language is { } lang) builder.Append($" xml:lang=\"{Escape(lang)}\"");
        builder.Append('>').Append("<head><metadata>");
        foreach (var agent in agents)
        {
            builder.Append($"<ttm:agent type=\"person\" xml:id=\"{Escape(agent.Id)}\"");
            if (agent.Name is null) builder.Append("/>");
            else builder.Append("><ttm:name type=\"full\">").Append(Escape(agent.Name)).Append("</ttm:name></ttm:agent>");
        }
        builder.Append("</metadata></head>");
        builder.Append($"<body dur=\"{Time(end)}\"><div begin=\"{Time(lyrics.Lines.Count > 0 ? lyrics.Lines[0].StartMs : 0)}\" end=\"{Time(end)}\">");
        foreach (var line in lyrics.Lines)
        {
            builder.Append($"<p begin=\"{Time(line.StartMs)}\" end=\"{Time(line.EndMs)}\"");
            var agentId = line.Agent ?? (agents.Count > 0 ? agentBySide.GetValueOrDefault(line.Side) : null);
            if (agentId is not null) builder.Append($" ttm:agent=\"{Escape(agentId)}\"");
            if (line.Language is { } lineLang) builder.Append($" xml:lang=\"{Escape(lineLang)}\"");
            builder.Append('>');
            if (line.Words.Count == 0) builder.Append(Escape(line.Text));
            else AppendWords(builder, line.Words);
            if (line.Background is { } background)
            {
                builder.Append("<span ttm:role=\"x-bg\">");
                AppendWords(builder, background.Words);
                builder.Append("</span>");
            }
            if (line.Translation is { } translation) builder.Append("<span ttm:role=\"x-translation\">").Append(Escape(translation)).Append("</span>");
            if (line.Transliteration is { } roman) builder.Append("<span ttm:role=\"x-roman\">").Append(Escape(roman)).Append("</span>");
            builder.Append("</p>");
        }
        builder.Append("</div></body></tt>");
        return builder.ToString();
    }

    private static void AppendWords(StringBuilder builder, IReadOnlyList<SyncedWord> words)
    {
        foreach (var word in words)
        {
            builder.Append($"<span begin=\"{Time(word.StartMs)}\" end=\"{Time(word.EndMs)}\">").Append(Escape(word.Text.TrimEnd())).Append("</span>");
            // Пробел между словами — текстовый узел между span, как пишет Apple
            if (word.Text.Length > 0 && char.IsWhiteSpace(word.Text[^1])) builder.Append(' ');
        }
    }

    private static SyncedLine? ParseLine(XmlElement p)
    {
        var words = new List<SyncedWord>();
        var backgroundWords = new List<SyncedWord>();
        var backgroundText = "";
        var plain = new StringBuilder();
        string? translation = null, transliteration = null;

        void Collect(XmlNode parent, List<SyncedWord> target, StringBuilder? plainText)
        {
            foreach (XmlNode node in parent.ChildNodes)
            {
                switch (node.NodeType)
                {
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        var value = node.Value ?? "";
                        if (target.Count > 0 && value.Length > 0 && string.IsNullOrWhiteSpace(value))
                        {
                            if (!target[^1].Text.EndsWith(' ')) target[^1] = target[^1] with { Text = target[^1].Text + " " };
                        }
                        else plainText?.Append(value);
                        break;
                    case XmlNodeType.Element when node is XmlElement element && element.LocalName == "span":
                        switch (Attr(element, NsTtm, "role"))
                        {
                            case "x-bg":
                                Collect(element, backgroundWords, null);
                                if (backgroundWords.Count == 0) backgroundText = element.InnerText.Trim();
                                break;
                            case "x-translation":
                                translation = element.InnerText.Trim() is { Length: > 0 } tr ? tr : null;
                                break;
                            case "x-roman":
                                transliteration = element.InnerText.Trim() is { Length: > 0 } ro ? ro : null;
                                break;
                            default:
                                var begin = Attr(element, null, "begin") is { } b ? ParseTime(b) : null;
                                var end = Attr(element, null, "end") is { } en ? ParseTime(en) : null;
                                if (begin is not null && end is not null) target.Add(new SyncedWord(begin.Value, end.Value, element.InnerText));
                                else Collect(element, target, plainText);
                                break;
                        }
                        break;
                }
            }
        }

        Collect(p, words, plain);
        var start = (Attr(p, null, "begin") is { } pb ? ParseTime(pb) : null) ?? (words.Count > 0 ? words[0].StartMs : null);
        var stop = (Attr(p, null, "end") is { } pe ? ParseTime(pe) : null) ?? (words.Count > 0 ? words[^1].EndMs : null);
        if (start is null || stop is null) return null;
        var text = words.Count == 0 ? plain.ToString().Trim() : string.Concat(words.Select(w => w.Text)).Trim();
        if (text.Length == 0) return null;
        BackingVocals? background = backgroundWords.Count > 0
            ? new BackingVocals(backgroundWords[0].StartMs, backgroundWords[^1].EndMs, TrimLast(backgroundWords))
            : backgroundText.Length > 0 ? new BackingVocals(start.Value, stop.Value, [new SyncedWord(start.Value, stop.Value, backgroundText)]) : null;
        return new SyncedLine
        {
            StartMs = start.Value,
            EndMs = stop.Value,
            Text = text,
            Words = TrimLast(words),
            Agent = Attr(p, NsTtm, "agent"),
            Language = Attr(p, NsXml, "lang"),
            Background = background,
            Translation = translation,
            Transliteration = transliteration,
        };
    }

    /// <summary>У последнего слова нет пробела в конце.</summary>
    private static List<SyncedWord> TrimLast(List<SyncedWord> words) =>
        words.Count == 0 ? words : [.. words.Take(words.Count - 1), words[^1] with { Text = words[^1].Text.TrimEnd() }];

    /// <summary>Время TTML: часы («1:02:03.450», «02:03.45», «3.5») или смещение («12.3s», «450ms», «2m», «1h»).</summary>
    public static long? ParseTime(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        try
        {
            if (text.EndsWith("ms", StringComparison.Ordinal)) return (long)D(text[..^2]);
            if (text.EndsWith('s')) return (long)(D(text[..^1]) * 1000);
            if (text.EndsWith('m')) return (long)(D(text[..^1]) * 60_000);
            if (text.EndsWith('h')) return (long)(D(text[..^1]) * 3_600_000);
            var parts = text.Split(':');
            var seconds = D(parts[^1]);
            var minutes = parts.Length >= 2 ? long.Parse(parts[^2], CultureInfo.InvariantCulture) : 0;
            var hours = parts.Length >= 3 ? long.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
            return (hours * 3600 + minutes * 60) * 1000 + (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string Time(long ms)
    {
        var total = Math.Max(0, ms);
        var hours = total / 3_600_000;
        var minutes = total / 60_000 % 60;
        var seconds = total / 1000 % 60;
        var millis = total % 1000;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{millis:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{millis:000}");
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string? Attr(XmlElement element, string? ns, string name)
    {
        var value = ns is null ? element.GetAttribute(name) : element.GetAttribute(name, ns);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Все элементы ниже этого, в порядке документа.</summary>
    private static IEnumerable<XmlElement> Elements(XmlElement element)
    {
        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is not XmlElement e) continue;
            yield return e;
            foreach (var inner in Elements(e)) yield return inner;
        }
    }
}

/// <summary>Парсер файла текста по содержимому.</summary>
public static class LyricsFormats
{
    public enum Format
    {
        Ttml,
        Lrc,
        Plain,
    }

    public static Format Detect(string text) => TtmlFormat.Matches(text) ? Format.Ttml : LrcFormat.Matches(text) ? Format.Lrc : Format.Plain;

    /// <summary>Синхронный текст из TTML или LRC; null для простого текста или битого файла.</summary>
    public static SyncedLyrics? ParseSynced(string text) => Detect(text) switch
    {
        Format.Ttml => TtmlFormat.Parse(text),
        Format.Lrc => LrcFormat.Parse(text),
        _ => null,
    };
}
