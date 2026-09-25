using System.Text.Json.Nodes;

namespace Melogold.InnerTube;

/// <summary>Аудиоформат из <c>streamingData.adaptiveFormats</c> с прямой ссылкой (без <c>signatureCipher</c>).</summary>
public sealed record AudioFormat(int Itag, string Url, string MimeType, long? ContentLength, int? Bitrate);

/// <summary>Ответ <c>/player</c>: можно ли играть, форматы, громкость и длительность.</summary>
public sealed record PlayerResponse(
    string Status,
    string? Reason,
    IReadOnlyList<AudioFormat> AudioFormats,
    double? LoudnessDb,
    long? DurationMs);

public static class PlayerRequests
{
    /// <summary>
    /// <c>/youtubei/v1/player</c> клиентом <paramref name="profile"/>. Клиенты вроде IOS и ANDROID_VR отдают прямые ссылки:
    /// расшифровка подписи и JS-проверки YouTube им не нужны.
    /// </summary>
    public static async Task<PlayerResponse> PlayerAsync(this InnerTubeClient client, ClientProfile profile, string videoId, CancellationToken ct = default)
    {
        var response = await client.PostAsync(profile, "player",
            new JsonObject { ["videoId"] = videoId, ["contentCheckOk"] = true, ["racyCheckOk"] = true }, ct).ConfigureAwait(false);
        var formats = response.Items("streamingData", "adaptiveFormats")
            .Where(f => f.Str("mimeType")?.StartsWith("audio/", StringComparison.Ordinal) == true && f.Str("url") is not null)
            .Select(f => new AudioFormat((int)(f.Long("itag") ?? 0), f.Str("url")!, f.Str("mimeType")!, f.Long("contentLength"), (int?)f.Long("bitrate")))
            .ToList();
        var loudness = response.At("playerConfig", "audioConfig", "loudnessDb") is JsonValue v && v.TryGetValue<double>(out var db) ? db : (double?)null;
        var seconds = response.Long("videoDetails", "lengthSeconds");
        return new PlayerResponse(
            response.Str("playabilityStatus", "status") ?? "ERROR",
            response.Str("playabilityStatus", "reason"),
            formats,
            loudness,
            seconds is > 0 ? seconds * 1000 : null);
    }
}
