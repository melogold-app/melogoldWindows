using System.Text.Json.Nodes;
using Melogold.Core.Lyrics;
using Xunit;

namespace Melogold.Tests;

/// <summary>Форматы текстов по <c>spec/lyrics.vectors.json</c> (те же векторы у Android и сервера) и запись в оба формата.</summary>
public class LyricsFormatTests
{
    private static JsonNode? Shape(SyncedLyrics? lyrics)
    {
        if (lyrics is null) return null;
        static JsonArray Words(IEnumerable<SyncedWord> words) => new([.. words.Select(w => (JsonNode)new JsonArray(w.StartMs, w.EndMs, w.Text))]);
        return new JsonObject
        {
            ["timing"] = lyrics.Timing.ToString(),
            ["language"] = lyrics.Language,
            ["agents"] = new JsonArray([.. lyrics.Agents.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["side"] = a.Side.ToString(), ["name"] = a.Name })]),
            ["lines"] = new JsonArray([.. lyrics.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["startMs"] = l.StartMs,
                ["endMs"] = l.EndMs,
                ["text"] = l.Text,
                ["words"] = Words(l.Words),
                ["side"] = l.Side.ToString(),
                ["agent"] = l.Agent,
                ["language"] = l.Language,
                ["background"] = l.Background is { } b ? new JsonObject { ["startMs"] = b.StartMs, ["endMs"] = b.EndMs, ["words"] = Words(b.Words) } : null,
                ["translation"] = l.Translation,
                ["transliteration"] = l.Transliteration,
            })]),
        };
    }

    [Fact]
    public void Vectors()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spec", "lyrics.vectors.json")))!;
        var cases = root["cases"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var id = c!["id"]!.GetValue<string>();
            var actual = Shape(LyricsFormats.ParseSynced(c["input"]!.GetValue<string>()));
            var expected = c["expected"];
            Assert.True(JsonNode.DeepEquals(expected, actual), $"{id}:\nexpected {expected?.ToJsonString()}\nactual   {actual?.ToJsonString()}");
        }
    }

    [Fact]
    public void WrittenFormatsReadBack()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spec", "lyrics.vectors.json")))!;
        foreach (var c in root["cases"]!.AsArray())
        {
            if (LyricsFormats.ParseSynced(c!["input"]!.GetValue<string>()) is not { } lyrics) continue;
            var ttml = TtmlFormat.Parse(TtmlFormat.Write(lyrics));
            Assert.NotNull(ttml);
            Assert.Equal(lyrics.Lines.Select(l => (l.StartMs, l.EndMs, l.Text)), ttml.Lines.Select(l => (l.StartMs, l.EndMs, l.Text)));
            var lrc = LrcFormat.Parse(LrcFormat.Write(lyrics));
            Assert.NotNull(lrc);
            Assert.Equal(lyrics.Lines.Select(l => (l.StartMs, l.Text)), lrc.Lines.Select(l => (l.StartMs, l.Text)));
        }
    }
}
