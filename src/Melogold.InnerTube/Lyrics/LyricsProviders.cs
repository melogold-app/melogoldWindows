using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Melogold.InnerTube.Lyrics;

/// <summary>Трек LRCLIB с текстами.</summary>
public sealed record LrcLibTrack(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("trackName")] string TrackName,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

/// <summary>
/// LRCLIB (Android <c>providers/lrclib</c>): поиск по названию и исполнителю без альбома — YouTube Music называет
/// альбомы по-своему, и одно слово мимо ничего не находит.
/// </summary>
public sealed class LrcLib(HttpClient http)
{
    public const string BaseUrl = "https://lrclib.net";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client", userAgent);
        return client;
    }

    public async Task<List<LrcLibTrack>> SearchAsync(string artist, string title, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LrcLibTrack>>(
            $"{BaseUrl}/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", Json, ct).ConfigureAwait(false) ?? [];

    /// <summary>Треки по запросу, у которых есть хоть какой-то текст («Найти текст»).</summary>
    public async Task<List<LrcLibTrack>> SearchAsync(string query, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<List<LrcLibTrack>>($"{BaseUrl}/api/search?q={Uri.EscapeDataString(query)}", Json, ct).ConfigureAwait(false) ?? [])
        .Where(t => !string.IsNullOrWhiteSpace(t.SyncedLyrics) || !string.IsNullOrWhiteSpace(t.PlainLyrics)).ToList();

    /// <summary>Текст версии трека, ближайшей по длительности; синхронный — только в пределах max(3 с, 10 %).</summary>
    public async Task<string?> BestLyricsAsync(string artist, string title, long durationMs, bool synced, CancellationToken ct = default)
    {
        var tracks = (await SearchAsync(artist, title, ct).ConfigureAwait(false))
            .Where(t => synced ? t.SyncedLyrics is not null : t.PlainLyrics is not null).ToList();
        var best = BestMatching(tracks, title, durationMs);
        return synced ? best?.SyncedLyrics : best?.PlainLyrics;
    }

    /// <summary>
    /// Версия с ближайшей длительностью, если она близко (3 с или 10 %): синхронный текст записи другой длины (живой,
    /// сокращённой) уезжает. Без длительности — с ближайшим по длине названием.
    /// </summary>
    public static LrcLibTrack? BestMatching(IReadOnlyList<LrcLibTrack> tracks, string title, long durationMs)
    {
        var seconds = durationMs / 1000;
        if (seconds <= 0) return tracks.OrderBy(t => Math.Abs(t.TrackName.Length - title.Length)).FirstOrDefault();
        var closest = tracks.OrderBy(t => Math.Abs(t.Duration - seconds)).FirstOrDefault();
        return closest is not null && Math.Abs(closest.Duration - seconds) <= Math.Max(3.0, seconds * 0.1) ? closest : null;
    }
}

/// <summary>KuGou (Android <c>providers/kugou</c>): последняя линия синхронного текста.</summary>
public sealed class KuGou(HttpClient http)
{
    private sealed record SongInfo([property: JsonPropertyName("duration")] long Duration, [property: JsonPropertyName("hash")] string Hash);

    private sealed record SongData([property: JsonPropertyName("info")] List<SongInfo> Info);

    private sealed record SongResponse([property: JsonPropertyName("data")] SongData Data);

    private sealed record Candidate([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("accesskey")] string AccessKey);

    private sealed record CandidatesResponse([property: JsonPropertyName("candidates")] List<Candidate> Candidates);

    private sealed record DownloadResponse([property: JsonPropertyName("content")] string Content);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36" } },
    };

    /// <summary>LRC трека: сначала песня с совпадающей длительностью (допуск до 5 с), потом поиск текста по словам.</summary>
    public async Task<string?> LyricsAsync(string artist, string title, long durationSeconds, CancellationToken ct = default)
    {
        var keyword = Keyword(artist, title);
        var songs = await GetAsync<SongResponse>(
            $"https://mobileservice.kugou.com/api/v3/search/song?version=9108&plat=0&pagesize=8&showtype=0&keyword={Uri.EscapeDataString(keyword)}", ct).ConfigureAwait(false);
        var infos = songs?.Data?.Info ?? [];
        for (var tolerance = 0; tolerance <= 5 && infos.Count > 0; tolerance++)
        {
            foreach (var info in infos.Where(i => i.Duration >= durationSeconds - tolerance && i.Duration <= durationSeconds + tolerance))
            {
                var byHash = await GetAsync<CandidatesResponse>($"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&hash={info.Hash}", ct).ConfigureAwait(false);
                if (byHash?.Candidates.FirstOrDefault() is { } candidate) return await DownloadAsync(candidate, ct).ConfigureAwait(false);
            }
        }
        var byKeyword = await GetAsync<CandidatesResponse>($"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={Uri.EscapeDataString(keyword)}", ct).ConfigureAwait(false);
        return byKeyword?.Candidates.FirstOrDefault() is { } found ? await DownloadAsync(found, ct).ConfigureAwait(false) : null;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        // KuGou отвечает JSON-ом с типом text/plain или text/html
        var text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    private async Task<string?> DownloadAsync(Candidate candidate, CancellationToken ct)
    {
        var response = await GetAsync<DownloadResponse>($"https://krcs.kugou.com/download?ver=1&man=yes&client=pc&fmt=lrc&id={candidate.Id}&accesskey={candidate.AccessKey}", ct).ConfigureAwait(false);
        return response is null ? null : Normalize(Encoding.UTF8.GetString(Convert.FromBase64String(response.Content)));
    }

    private static string Keyword(string artist, string title)
    {
        var (newTitle, featuring) = Extract(title, " (feat. ", ')');
        var newArtist = (featuring.Length == 0 ? artist : $"{artist}, {featuring}")
            .Replace(", ", "、", StringComparison.Ordinal).Replace(" & ", "、", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        return $"{newArtist} - {newTitle}";
    }

    private static (string Remaining, string Inner) Extract(string text, string start, char end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return (text, "");
        var to = text.IndexOf(end, from);
        if (to < 0) return (text, "");
        return (text.Remove(from, to - from + 1), text.Substring(from + start.Length, to - from - start.Length));
    }

    private static readonly string[] MetaPrefixes = ["[ti:", "[ar:", "[al:", "[by:", "[hash:", "[sign:", "[qq:", "[total:", "[offset:", "[id:"];
    private static readonly string[] CreditMarkers = ["]Written by：", "]Lyrics by：", "]Composed by：", "]Producer：", "]作曲 : ", "]作词 : "];

    /// <summary>Без служебных тегов и титров в начале (авторы, композитор), как у Android.</summary>
    internal static string Normalize(string value)
    {
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        int toDrop = 0, maybeToDrop = 0;
        foreach (var line in text.Split('\n'))
        {
            if (MetaPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)) || CreditMarkers.Any(m => line.Length >= 9 + m.Length && string.CompareOrdinal(line, 9, m, 0, m.Length) == 0))
            {
                toDrop += line.Length + 1 + maybeToDrop;
                maybeToDrop = 0;
            }
            else if (maybeToDrop == 0) maybeToDrop = line.Length + 1;
            else
            {
                maybeToDrop = 0;
                break;
            }
        }
        var drop = Math.Min(text.Length, toDrop + maybeToDrop);
        return text[drop..].Replace("&apos;", "'", StringComparison.Ordinal);
    }
}
