using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Melogold.InnerTube;

namespace Melogold.Playback;

/// <summary>
/// Список клиентов InnerTube для потока (docs/PROMPT.md §4): <c>config/stream-clients.json</c> в репозитории. При старте
/// берётся сохранённая копия, потом свежий файл с <c>main</c> — поломку на стороне YouTube можно чинить без релиза.
/// Встроенный список (<see cref="BuiltIn"/>) остаётся запасным: негодный файл не применяется. Здесь же — сброс кэша
/// адресов потока при смене сети (адреса googlevideo привязаны к адресу клиента).
/// </summary>
public sealed class StreamClients(StreamResolver resolver, string cachePath, Action<string, Exception?> log)
{
    public const string Url = "https://raw.githubusercontent.com/melogold-app/melogoldWindows/main/config/stream-clients.json";
    private const int Schema = 1;

    public static IReadOnlyList<ClientProfile> BuiltIn { get; } = [ClientProfile.VisionOs];

    private sealed record Entry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("host")] string? Host,
        [property: JsonPropertyName("userAgent")] string? UserAgent,
        [property: JsonPropertyName("referer")] string? Referer,
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("deviceMake")] string? DeviceMake,
        [property: JsonPropertyName("deviceModel")] string? DeviceModel,
        [property: JsonPropertyName("osName")] string? OsName,
        [property: JsonPropertyName("osVersion")] string? OsVersion,
        [property: JsonPropertyName("androidSdkVersion")] int? AndroidSdkVersion,
        [property: JsonPropertyName("mediaUserAgent")] string? MediaUserAgent);

    private sealed record Config(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("clients")] List<Entry>? Clients);

    /// <summary>Сохранённый список сразу, свежий — в фоне; кэш адресов сбрасывается при смене сети.</summary>
    public void Start(string userAgent)
    {
        resolver.Clients = BuiltIn;
        try
        {
            if (File.Exists(cachePath) && Parse(File.ReadAllText(cachePath)) is { } saved) resolver.Clients = saved;
        }
        catch (IOException e)
        {
            log("Stream clients cache unreadable", e);
        }
        NetworkChange.NetworkAddressChanged += (_, _) => resolver.InvalidateAll();
        _ = RefreshAsync(userAgent);
    }

    private async Task RefreshAsync(string userAgent)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            var text = await http.GetStringAsync(Url).ConfigureAwait(false);
            if (Parse(text) is not { } fresh)
            {
                log("Stream clients from GitHub rejected", null);
                return;
            }
            resolver.Clients = fresh;
            var temp = cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, text).ConfigureAwait(false);
            File.Move(temp, cachePath, true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log("Stream clients not refreshed", e);
        }
    }

    /// <summary>Профили из файла; null — не та схема, пустой список или клиент без обязательных полей.</summary>
    public static IReadOnlyList<ClientProfile>? Parse(string json)
    {
        Config? config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (config is not { Schema: Schema, Clients: { Count: > 0 } clients }) return null;
        var profiles = new List<ClientProfile>();
        foreach (var c in clients)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Id <= 0 || string.IsNullOrWhiteSpace(c.Version) || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.UserAgent))
                return null;
            profiles.Add(new ClientProfile(c.Name, c.Id, c.Version, c.Host, c.UserAgent, c.Referer, c.Platform, c.DeviceMake, c.DeviceModel,
                c.OsName, c.OsVersion, c.AndroidSdkVersion, c.MediaUserAgent));
        }
        return profiles;
    }
}
