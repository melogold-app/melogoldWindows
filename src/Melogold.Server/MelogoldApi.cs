using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Melogold.Server;

/// <summary>
/// Ошибка сервера Melogold: HTTP-статус и <c>code</c> конверта (API §2) — единственное, по чему ветвится клиент.
/// <see cref="Status"/> 0 — ответа не было (нет сети, DNS, TLS, таймаут).
/// </summary>
public sealed class ApiException(int status, string? code, string? message, ErrorEnvelope? envelope = null, Exception? inner = null)
    : Exception(message ?? code ?? $"HTTP {status}", inner)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public ErrorEnvelope? Envelope { get; } = envelope;

    public bool IsNetwork => Status == 0;

    /// <summary>Сеть, 5xx, 429: данные и токены не трогаются, попытка повторяется позже.</summary>
    public bool IsTransient => IsNetwork || Status >= 500 || Status == 429;

    public int? RetryAfterSeconds => Envelope?.RetryAfterSeconds;
}

/// <summary>
/// HTTP-клиент API Melogold (контракт <c>melogoldServer/docs/API.md</c>). User-Agent <c>melogold-windows/&lt;версия&gt;</c>
/// (API §1.2), <c>Accept-Language</c> — язык системы, <c>X-Sync-Protocol: 1</c> там, где он обязателен.
/// </summary>
public sealed class MelogoldApi : IDisposable
{
    /// <summary>Протокол синхронизации и playback этого клиента (API §1.1).</summary>
    public const int SyncProtocol = 1;

    /// <summary>Версия HTTP API, которую понимает клиент.</summary>
    public const int ApiVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly HttpClient _stream;

    public MelogoldApi(string baseUrl, string clientVersion, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        var userAgent = new ProductInfoHeaderValue("melogold-windows", clientVersion);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(35),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(userAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue(CultureInfo.CurrentUICulture.Name.Length > 0 ? CultureInfo.CurrentUICulture.Name : "en"));
        _stream = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        _stream.DefaultRequestHeaders.UserAgent.Add(userAgent);
    }

    public string BaseUrl { get; }

    // ---------- Общие вызовы ----------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? token, bool sync, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var response = await RawAsync(method, path, body, token, sync, ct, timeout).ConfigureAwait(false);
        try
        {
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
        }
        catch (JsonException e)
        {
            throw new ApiException((int)response.StatusCode, "invalid_response", e.Message, inner: e);
        }
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, string? token, bool sync, CancellationToken ct)
    {
        using var _ = await RawAsync(method, path, body, token, sync, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body, string? token, bool sync, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (sync) request.Headers.Add("X-Sync-Protocol", SyncProtocol.ToString(CultureInfo.InvariantCulture));
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
            request.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }, Json), Encoding.UTF8, "application/json");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linked.CancelAfter(t);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(0, "network", e.Message, inner: e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(0, "timeout", "Timeout", inner: e);
        }

        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            ErrorEnvelope? envelope = null;
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                try
                {
                    envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                }
            }
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : (int?)null;
            if (envelope is not null && envelope.RetryAfterSeconds is null && retryAfter is not null) envelope = envelope with { RetryAfterSeconds = retryAfter };
            // Не JSON (Caddy, шлюз): решает статус (API §1.2)
            throw new ApiException((int)response.StatusCode, envelope?.Code ?? $"http_{(int)response.StatusCode}", envelope?.Message, envelope);
        }
    }

    // ---------- Сервер (API §4.2) ----------

    public Task<ServerInfo> ServerInfoAsync(CancellationToken ct = default) =>
        SendAsync<ServerInfo>(HttpMethod.Get, "/server/info", null, null, false, ct, TimeSpan.FromSeconds(10));

    // ---------- Регистрация, вход, сессии (API §4.3) ----------

    public Task<RegisterChallenge> RegisterChallengeAsync(CancellationToken ct = default) =>
        SendAsync<RegisterChallenge>(HttpMethod.Get, "/auth/register/challenge", null, null, false, ct);

    public Task<AuthSession> RegisterAsync(string login, string password, DeviceInput device, PowSolution? pow, CancellationToken ct = default) =>
        SendAsync<AuthSession>(HttpMethod.Post, "/auth/register", new { login, password, device, pow }, null, false, ct);

    public Task<AuthSession> LoginAsync(string login, string password, DeviceInput device, CancellationToken ct = default) =>
        SendAsync<AuthSession>(HttpMethod.Post, "/auth/login", new { login, password, device }, null, false, ct);

    public Task<RefreshResponse> RefreshAsync(string refreshToken, DevicePatch device, CancellationToken ct = default) =>
        SendAsync<RefreshResponse>(HttpMethod.Post, "/auth/refresh", new { refreshToken, device }, null, false, ct);

    public Task LogoutAsync(string refreshToken, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "/auth/logout", new { refreshToken }, null, false, ct);

    public Task<AuthSession> RecoverAsync(string login, string recoveryCode, string newPassword, DeviceInput device, CancellationToken ct = default) =>
        SendAsync<AuthSession>(HttpMethod.Post, "/auth/recover", new { login, recoveryCode, newPassword, device }, null, false, ct);

    public Task<MeResponse> MeAsync(string token, CancellationToken ct = default) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/auth/me", null, token, false, ct);

    // ---------- Устройства и аккаунт (API §4.4–§4.5) ----------

    public Task<DeviceListResponse> DevicesAsync(string token, CancellationToken ct = default) =>
        SendAsync<DeviceListResponse>(HttpMethod.Get, "/auth/me/devices", null, token, false, ct);

    public Task<DeviceDto> RenameDeviceAsync(string token, string deviceId, string? name, string? password, CancellationToken ct = default) =>
        SendAsync<DeviceDto>(HttpMethod.Patch, $"/auth/me/devices/{deviceId}", new { name, password }, token, false, ct);

    public Task RevokeDeviceAsync(string token, string deviceId, string? password, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, $"/auth/me/devices/{deviceId}/revoke", new { password }, token, false, ct);

    public Task<RevokeOthersResponse> RevokeOthersAsync(string token, string? password, CancellationToken ct = default) =>
        SendAsync<RevokeOthersResponse>(HttpMethod.Post, "/auth/me/devices/revoke-others", new { password }, token, false, ct);

    public Task<ChangePasswordResponse> ChangePasswordAsync(string token, string? currentPassword, string newPassword, bool signOutOtherDevices, CancellationToken ct = default) =>
        SendAsync<ChangePasswordResponse>(HttpMethod.Post, "/auth/me/password", new { currentPassword, newPassword, signOutOtherDevices }, token, false, ct);

    public Task<RecoveryCodeResponse> RotateRecoveryCodeAsync(string token, string password, CancellationToken ct = default) =>
        SendAsync<RecoveryCodeResponse>(HttpMethod.Post, "/auth/me/recovery-code", new { password }, token, false, ct);

    public Task ConfirmRecoveryCodeAsync(string token, string recoveryCodeCreatedAt, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "/auth/me/recovery-code/confirm", new { recoveryCodeCreatedAt }, token, false, ct);

    public Task DeleteAccountAsync(string token, string password, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "/auth/me/delete", new { password }, token, false, ct);

    // ---------- Синхронизация (API §4.7–§4.8) ----------

    public Task<SyncSummary> SyncSummaryAsync(string token, CancellationToken ct = default) =>
        SendAsync<SyncSummary>(HttpMethod.Get, "/sync/summary", null, token, true, ct);

    public Task<MergePlanResponse> MergePlanAsync(string token, MergePlanRequest request, CancellationToken ct = default) =>
        SendAsync<MergePlanResponse>(HttpMethod.Post, "/sync/merge-plan", request, token, true, ct);

    public Task<SyncResponse> SyncAsync(string token, SyncRequest request, CancellationToken ct = default) =>
        SendAsync<SyncResponse>(HttpMethod.Post, "/sync", request, token, true, ct);

    // ---------- Playback (API §4.9) ----------

    public Task<PlaybackStateResponse> PlaybackStateAsync(string token, CancellationToken ct = default) =>
        SendAsync<PlaybackStateResponse>(HttpMethod.Get, "/playback/state", null, token, true, ct);

    public Task<PlaybackPutResult> PutPlaybackStateAsync(string token, PlaybackPut request, CancellationToken ct = default) =>
        SendAsync<PlaybackPutResult>(HttpMethod.Put, "/playback/state", request, token, true, ct);

    public Task ClearPlaybackStateAsync(string token, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, "/playback/state", null, token, true, ct);

    // ---------- SSE (API §6) ----------

    /// <summary>
    /// Живые события: кадры <c>id:</c> + <c>data:</c> без <c>event:</c>, heartbeat — комментарий. Поток заканчивается,
    /// когда сервер его закрывает (истёк токен, отзыв) — вызывающий переоткрывает с backoff.
    /// </summary>
    public async IAsyncEnumerable<LiveEvent> EventsAsync(string token, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/auth/me/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage response;
        try
        {
            response = await _stream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(0, "network", e.Message, inner: e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ErrorEnvelope? envelope = null;
                try
                {
                    envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                }
                catch (NotSupportedException)
                {
                }
                throw new ApiException((int)response.StatusCode, envelope?.Code, envelope?.Message, envelope);
            }
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(body, Encoding.UTF8);
            var data = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    yield break;
                }
                if (line is null) yield break;
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        LiveEvent? parsed = null;
                        try
                        {
                            parsed = JsonSerializer.Deserialize<LiveEvent>(data.ToString(), Json);
                        }
                        catch (JsonException)
                        {
                        }
                        data.Clear();
                        if (parsed is not null) yield return parsed;
                    }
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _stream.Dispose();
    }
}
