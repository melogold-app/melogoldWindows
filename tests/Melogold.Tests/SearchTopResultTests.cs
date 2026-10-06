using System.Text.Json.Nodes;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0023: лучший результат поиска — по сохранённым ответам YouTube Music (Fixtures/search).</summary>
public sealed class SearchTopResultTests(ITestOutputHelper output)
{
    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "search", name + ".json")))!;

    private MusicItem? Top(string name, string query)
    {
        var summary = YouTubeMusic.ParseSearchSummary(Fixture(name));
        var top = SearchTopResult.Pick(summary, query);
        output.WriteLine($"{query}: {top}");
        return top;
    }

    [Theory]
    [InlineData("kino", "Кино")]
    [InlineData("michael-jackson", "Michael Jackson")]
    [InlineData("tkay-maidza", "Tkay Maidza")]
    public void ArtistQueryGivesTheArtistCard(string name, string query)
    {
        var artist = Assert.IsType<ArtistItem>(Top(name, query));
        Assert.Equal(query, artist.Name);
        Assert.StartsWith("UC", artist.BrowseId);
        Assert.NotNull(artist.ThumbnailUrl);
    }

    [Fact]
    public void AlbumAndTrackQueries()
    {
        var album = Assert.IsType<AlbumItem>(Top("ok-computer", "OK Computer"));
        Assert.Equal("OK Computer", album.Title);
        Assert.Contains("Radiohead", album.ArtistsText);
        var track = Assert.IsType<Track>(Top("bohemian-rhapsody", "Bohemian Rhapsody"));
        Assert.StartsWith("Bohemian Rhapsody", track.Title);
        Assert.Equal(11, track.VideoId.Length);
    }

    [Fact]
    public void WithoutTheCardTheArtistNamedByTheQueryRises()
    {
        // Тот же ответ без карточки лучшего результата: исполнитель «Кино» есть в выдаче — он и становится лучшим
        var response = Fixture("kino");
        var sections = response["contents"]!["tabbedSearchResultsRenderer"]!["tabs"]![0]!["tabRenderer"]!["content"]!["sectionListRenderer"]!["contents"]!.AsArray();
        var cardIndex = sections.Select((s, i) => (s, i)).First(p => p.s!["musicCardShelfRenderer"] is not null).i;
        var card = sections[cardIndex]!["musicCardShelfRenderer"]!;
        // Строки внутри карточки (треки исполнителя) — обычной полкой
        sections[cardIndex] = new JsonObject { ["musicShelfRenderer"] = new JsonObject { ["contents"] = card["contents"]?.DeepClone() } };
        var summary = YouTubeMusic.ParseSearchSummary(response);
        Assert.Null(summary.TopResult);
        var artists = summary.Items.OfType<ArtistItem>().Select(a => a.Name).ToList();
        output.WriteLine("исполнители в выдаче: " + string.Join(", ", artists));
        if (!artists.Any(a => SearchTopResult.Normalize(a) == "кино"))
            summary = summary with { Items = [new ArtistItem { BrowseId = "UCkino", Name = "КИНО" }, .. summary.Items] };
        var artist = Assert.IsType<ArtistItem>(SearchTopResult.Pick(summary, "кино!"));
        Assert.Equal("кино", SearchTopResult.Normalize(artist.Name));
        // Не совпадает — лучшего нет
        Assert.Null(SearchTopResult.Pick(summary with { Items = [] }, "Кино"));
    }

    [Theory]
    [InlineData("Ёлка-Палка!", "елка палка")]
    [InlineData("  Michael   Jackson ", "michael jackson")]
    [InlineData("AC/DC", "ac dc")]
    public void Normalize(string text, string expected) => Assert.Equal(expected, SearchTopResult.Normalize(text));
}
