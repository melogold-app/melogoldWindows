using Melogold.Core.Domain;
using Melogold.Core.Music;
using System.Text.Json.Nodes;

namespace Melogold.InnerTube;

/// <summary>Навигация по ответам InnerTube: пути, <c>runs</c>, обложки. Всё терпит отсутствующие ключи.</summary>
public static class J
{
    /// <summary>Узел по пути: строки — ключи объектов, числа — индексы массивов.</summary>
    public static JsonNode? At(this JsonNode? node, params object[] path)
    {
        foreach (var step in path)
        {
            node = step switch
            {
                string key when node is JsonObject obj => obj.TryGetPropertyValue(key, out var value) ? value : null,
                int index when node is JsonArray array => index >= 0 && index < array.Count ? array[index] : null,
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string? Str(this JsonNode? node, params object[] path)
    {
        var value = node.At(path);
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public static long? Long(this JsonNode? node, params object[] path)
    {
        var value = node.At(path);
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        return null;
    }

    public static bool Bool(this JsonNode? node, params object[] path)
    {
        var value = node.At(path);
        return value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public static IEnumerable<JsonNode> Items(this JsonNode? node, params object[] path) =>
        node.At(path) is JsonArray array ? array.Where(x => x is not null)! : [];

    /// <summary>Первое вложенное значение с ключом (обход в глубину).</summary>
    public static JsonNode? Find(this JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue(key, out var direct) && direct is not null) return direct;
                foreach (var (_, value) in obj)
                {
                    var found = value.Find(key);
                    if (found is not null) return found;
                }
                return null;
            case JsonArray array:
                foreach (var item in array)
                {
                    var found = item.Find(key);
                    if (found is not null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Все вложенные значения с ключом.</summary>
    public static IEnumerable<JsonNode> FindAll(this JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (value is null) continue;
                    if (name == key) yield return value;
                    else
                        foreach (var inner in value.FindAll(key)) yield return inner;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                foreach (var inner in item.FindAll(key))
                    yield return inner;
                break;
        }
    }

    /// <summary>Текст узла <c>{runs: [...]}</c> или <c>{simpleText}</c> или <c>{content}</c>.</summary>
    public static string? Text(this JsonNode? node)
    {
        if (node is null) return null;
        if (node.At("runs") is JsonArray runs) return string.Concat(runs.Select(r => r.Str("text")));
        return node.Str("simpleText") ?? node.Str("content");
    }

    public static IReadOnlyList<Run> Runs(this JsonNode? node) =>
        node.At("runs") is JsonArray runs ? runs.Where(r => r is not null).Select(r => new Run(r!)).ToList() : [];

    /// <summary>Самая большая обложка из <c>thumbnails</c>.</summary>
    public static string? BestThumbnail(this JsonNode? thumbnails)
    {
        if (thumbnails is not JsonArray array || array.Count == 0) return null;
        return array.Where(t => t is not null).OrderBy(t => t.Long("width") ?? 0).Last().Str("url");
    }
}

/// <summary>Кусок текста с возможным переходом.</summary>
public readonly record struct Run(JsonNode Node)
{
    public string Text => Node.Str("text") ?? "";
    public string? BrowseId => Node.Str("navigationEndpoint", "browseEndpoint", "browseId");
    public string? PageType => Node.Str("navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
    public string? WatchVideoId => Node.Str("navigationEndpoint", "watchEndpoint", "videoId");
    public bool IsSeparator => Text is " • " or " · " or "•";
}
