using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Обновить сохранённые ответы поиска YouTube Music (tasks/0023): <c>MELOGOLD_LIVE=1</c> и <c>MELOGOLD_SAVE_FIXTURES</c> =
/// папка <c>tests/Melogold.Tests/Fixtures/search</c>. Пять запросов <c>search</c>, ни одного <c>player</c>.
/// </summary>
public class LiveSearchFixtures
{
    [Fact]
    public async Task SaveSearchResponses()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1" && Environment.GetEnvironmentVariable("MELOGOLD_SAVE_FIXTURES") is { Length: > 0 }, "MELOGOLD_LIVE=1, MELOGOLD_SAVE_FIXTURES");
        var folder = Environment.GetEnvironmentVariable("MELOGOLD_SAVE_FIXTURES")!;
        Directory.CreateDirectory(folder);
        var client = new InnerTubeClient();
        (client.Language, client.Region) = InnerTubeClient.LocaleFrom(new CultureInfo("ru-RU"));
        foreach (var (name, query) in new[] { ("kino", "Кино"), ("michael-jackson", "Michael Jackson"), ("tkay-maidza", "Tkay Maidza"), ("bohemian-rhapsody", "Bohemian Rhapsody"), ("ok-computer", "OK Computer") })
        {
            var response = await client.PostAsync(ClientProfile.WebRemix, "search", new JsonObject { ["query"] = query }, TestContext.Current.CancellationToken);
            Strip(response);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".json"), response.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Без идентификатора посетителя (<c>responseContext.visitorData</c>) и полей отслеживания: в репозитории только
    /// выдача, по которой проверяется разбор.
    /// </summary>
    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).Where(Dropped.Contains).ToList()) obj.Remove(key);
                foreach (var (_, value) in obj) Strip(value);
                break;
            case JsonArray array:
                foreach (var value in array) Strip(value);
                break;
        }
    }

    private static readonly HashSet<string> Dropped = ["responseContext", "trackingParams", "clickTrackingParams", "loggingContext", "serviceTrackingParams", "adSignalsInfo"];
}
