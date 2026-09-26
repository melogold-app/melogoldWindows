using System.Text.Json.Nodes;

namespace Melogold.InnerTube;

/// <summary>
/// Почему YouTube не отдаёт видео — его собственными словами (задание 0010, Android <c>Playability.kt</c>): ответ
/// <c>player</c> клиента WEB, который присылает список стран, даже когда видео не играет. Клиенты потока (VISIONOS, IOS,
/// ANDROID_VR) этого списка не присылают. Спрашивается один раз, когда поток получить не удалось.
/// </summary>
/// <param name="Status"><c>OK</c>, <c>UNPLAYABLE</c>, <c>LOGIN_REQUIRED</c>, <c>ERROR</c>…</param>
/// <param name="Country">
/// страна, в которой YouTube видит устройство (из <c>visitorData</c>): VPN, который Google считает российским, даёт <c>RU</c>
/// </param>
/// <param name="AvailableCountries">где правообладатель открыл видео; пусто — YouTube не сказал</param>
public sealed record Playability(string? Status, string? Reason, string? Country, IReadOnlyList<string> AvailableCountries)
{
    /// <summary>Правообладатель закрыл видео в стране, где YouTube видит устройство.</summary>
    public bool IsBlockedHere => Country is not null && AvailableCountries.Count > 0 && !AvailableCountries.Contains(Country);

    public static Playability From(JsonNode response) => new(
        response.Str("playabilityStatus", "status"),
        response.Str("playabilityStatus", "reason"),
        VisitorCountry(response.Str("responseContext", "visitorData")),
        // WEB отвечает playerMicroformatRenderer, WEB_REMIX — microformatDataRenderer
        (response.At("microformat", "playerMicroformatRenderer", "availableCountries") ?? response.At("microformat", "microformatDataRenderer", "availableCountries"))
            is JsonArray countries
            ? countries.Select(c => c?.GetValue<string>()).OfType<string>().ToList()
            : []);

    /// <summary>
    /// Страна запроса из <c>visitorData</c> ответа: base64 (url-safe, <c>%3D</c> вместо <c>=</c>) protobuf, поле 6 —
    /// сообщение, в его поле 1 — код страны из двух букв (<c>CgtRTWpH…MigKAk5M…</c> → <c>NL</c>). null — нет или не разобрать.
    /// </summary>
    public static string? VisitorCountry(string? visitorData)
    {
        if (string.IsNullOrWhiteSpace(visitorData)) return null;
        var text = visitorData.Replace("%3D", "=", StringComparison.OrdinalIgnoreCase).TrimEnd('=').Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
        if (Field(bytes, 0, bytes.Length, 6) is not var (start, end) || Field(bytes, start, end, 1) is not var (s, e)) return null;
        var country = System.Text.Encoding.ASCII.GetString(bytes, s, e - s);
        return country.Length == 2 && country.All(char.IsAsciiLetterUpper) ? country : null;
    }

    /// <summary>Границы первого поля <paramref name="field"/> с длиной (тип 2) в сообщении <c>bytes[start, end)</c>.</summary>
    private static (int Start, int End)? Field(byte[] bytes, int start, int end, int field)
    {
        var index = start;
        while (index < end)
        {
            if (Varint(bytes, ref index, end) is not { } key) return null;
            switch (key & 7)
            {
                case 0:
                    if (Varint(bytes, ref index, end) is null) return null;
                    break;
                case 1:
                    index += 8;
                    break;
                case 2:
                    if (Varint(bytes, ref index, end) is not { } length || length > (ulong)(end - index)) return null;
                    if ((int)(key >> 3) == field) return (index, index + (int)length);
                    index += (int)length;
                    break;
                case 5:
                    index += 4;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static ulong? Varint(byte[] bytes, ref int index, int end)
    {
        ulong value = 0;
        for (var shift = 0; index < end && shift < 64; shift += 7)
        {
            var b = bytes[index++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        return null;
    }
}

public static class PlayabilityRequest
{
    /// <summary>
    /// Один запрос <c>player</c> клиентом WEB на <c>youtubei.googleapis.com</c>, без нашего <c>visitorData</c>: страну
    /// YouTube определяет заново по адресу. Ответ не проверяется на пригодность — он и есть диагноз. Таймаут 8 с.
    /// </summary>
    public static async Task<Playability> PlayabilityAsync(this InnerTubeClient client, string videoId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var response = await client.PostAsync(ClientProfile.Web, "player", new JsonObject { ["videoId"] = videoId }, timeout.Token,
            host: "youtubei.googleapis.com", anonymous: true).ConfigureAwait(false);
        return Playability.From(response);
    }
}
