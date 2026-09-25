using Melogold.Core.Domain;

namespace Melogold.Server;

/// <summary>Сессия на сервере, как её хранит устройство (файл шифруется DPAPI, см. приложение).</summary>
public sealed record StoredSession(
    string ServerUrl,
    string ServerId,
    string UserId,
    string Login,
    string DeviceId,
    string AccessToken,
    long AccessTokenExpiresAt,
    string RefreshToken);

/// <summary>Где хранится сессия. Приложение шифрует её для текущего пользователя Windows.</summary>
public interface ISessionStore
{
    StoredSession? Load();

    void Save(StoredSession? session);
}

/// <summary>Что устройство сообщает о себе серверу (API §4.1 <c>DeviceInput</c>).</summary>
public interface IDeviceIdentity
{
    /// <summary><c>MachineGuid|installSalt</c> (API §1.6): из него и <c>serverId</c> получается hwid.</summary>
    string PlatformId { get; }

    string DeviceName { get; }
    string? OsVersion { get; }
    string? Model { get; }
    string ClientVersion { get; }
}

public abstract record AccountState
{
    /// <summary>Без аккаунта: приложение работает само по себе, как ViTune.</summary>
    public sealed record SignedOut : AccountState;

    public sealed record SignedIn(string Login, string DeviceId, string ServerUrl) : AccountState;

    /// <summary>Сервер закончил сессию (отзыв, смена пароля): войти снова. Библиотека и привязка остаются (API §1.7).</summary>
    public sealed record AuthRequired(string Login, string ServerUrl) : AccountState;
}

/// <summary>
/// Аккаунт на сервере Melogold (API §4.3–§4.5): регистрация с доказательством работы, вход, токены и их обновление
/// (single-flight, за 60 с до истечения, один повтор вызова на <c>access_token_expired</c>), выход, устройства.
/// Экраны и синхронизация ходят на сервер только через <see cref="AuthorizedAsync{T}"/>.
/// </summary>
public sealed class AccountService : IDisposable
{
    /// <summary>Сервер по умолчанию, пока нет официального домена (как у Android, REWRITE §3.5.12).</summary>
    public const string DefaultServerUrl = "https://178-250-187-202.sslip.io";

    private const long RefreshEarlyMs = 60_000;

    private readonly ISessionStore _store;
    private readonly IDeviceIdentity _identity;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Lock _apiLock = new();
    private (string Url, MelogoldApi Api)? _api;
    private StoredSession? _session;
    private string _serverUrl;

    public AccountService(ISessionStore store, IDeviceIdentity identity, string? serverUrl)
    {
        _store = store;
        _identity = identity;
        _serverUrl = serverUrl ?? DefaultServerUrl;
        _session = store.Load();
        State = _session is null ? new AccountState.SignedOut() : new AccountState.SignedIn(_session.Login, _session.DeviceId, _session.ServerUrl);
    }

    public AccountState State { get; private set; }

    public event Action<AccountState>? StateChanged;

    public StoredSession? Session => _session;

    /// <summary>Сервер, с которым работает приложение (Настройки › Сервер).</summary>
    public string ServerUrl => _session?.ServerUrl ?? _serverUrl;

    /// <summary>Последний ответ <c>/server/info</c> текущего сервера.</summary>
    public ServerInfo? ServerInfo { get; private set; }

    public MelogoldApi Api(string? url = null)
    {
        url ??= ServerUrl;
        lock (_apiLock)
        {
            if (_api is { } cached && cached.Url == url) return cached.Api;
            var api = new MelogoldApi(url, _identity.ClientVersion);
            _api?.Api.Dispose();
            _api = (url, api);
            return api;
        }
    }

    /// <summary>
    /// Проверка сервера по API §7.1 п. 3–4: <c>software == "melogold-server"</c> и совместимые версии API и протокола.
    /// </summary>
    public async Task<ServerInfo> CheckAsync(string url, CancellationToken ct = default)
    {
        using var api = new MelogoldApi(url, _identity.ClientVersion);
        var info = await api.ServerInfoAsync(ct).ConfigureAwait(false);
        if (info.Software != "melogold-server") throw new ApiException(0, "not_melogold", "Not a Melogold server");
        if (info.MinApiVersion > MelogoldApi.ApiVersion) throw new ApiException(0, "client_outdated", "The server needs a newer app");
        if (info.ApiVersion < MelogoldApi.ApiVersion) throw new ApiException(0, "server_outdated", "The server is too old");
        if (info.Features?.Sync is { } sync && sync.MinProtocol > MelogoldApi.SyncProtocol) throw new ApiException(0, "client_outdated", "The server needs a newer app");
        if (url == ServerUrl) ServerInfo = info;
        return info;
    }

    /// <summary>Переключиться на другой сервер: сессия старого на этом устройстве заканчивается.</summary>
    public void SetServer(string url)
    {
        _serverUrl = url;
        ServerInfo = null;
        SetSession(null, new AccountState.SignedOut());
    }

    private async Task<(string ServerId, DeviceInput Device)> DeviceForServerAsync(CancellationToken ct)
    {
        var info = ServerInfo ?? await CheckAsync(ServerUrl, ct).ConfigureAwait(false);
        ServerInfo = info;
        return (info.ServerId, DeviceInput(info.ServerId));
    }

    public DeviceInput DeviceInput(string serverId) => new(
        Hwid.Compute(_identity.PlatformId, serverId),
        Utf16.Truncate(_identity.DeviceName, 64),
        "windows",
        Utf16.TruncateOrNull(_identity.OsVersion, 64),
        Utf16.TruncateOrNull(_identity.Model, 64),
        Utf16.Truncate(_identity.ClientVersion, 64));

    /// <summary>Создать аккаунт; ответ — код восстановления, он показывается один раз (API §4.3).</summary>
    public async Task<string> RegisterAsync(string login, string password, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (_, device) = await DeviceForServerAsync(ct).ConfigureAwait(false);
        var api = Api();
        var challenge = await api.RegisterChallengeAsync(ct).ConfigureAwait(false);
        PowSolution? pow = null;
        if (challenge.Bits > 0)
        {
            progress?.Report("pow");
            var nonce = await Task.Run(() => ProofOfWork.Solve(challenge.Challenge, challenge.Bits, ct), ct).ConfigureAwait(false);
            pow = new PowSolution(challenge.Challenge, nonce);
        }
        AuthSession session;
        try
        {
            session = await api.RegisterAsync(login, password, device, pow, ct).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Code is "pow_required" or "pow_invalid")
        {
            // Один повтор с новым вызовом (API §2.2)
            challenge = await api.RegisterChallengeAsync(ct).ConfigureAwait(false);
            var nonce = await Task.Run(() => ProofOfWork.Solve(challenge.Challenge, challenge.Bits, ct), ct).ConfigureAwait(false);
            session = await api.RegisterAsync(login, password, device, new PowSolution(challenge.Challenge, nonce), ct).ConfigureAwait(false);
        }
        Save(session);
        return session.RecoveryCode ?? "";
    }

    public async Task SignInAsync(string login, string password, CancellationToken ct = default)
    {
        var (_, device) = await DeviceForServerAsync(ct).ConfigureAwait(false);
        Save(await Api().LoginAsync(login, password, device, ct).ConfigureAwait(false));
    }

    /// <summary>Вход кодом восстановления с новым паролем: прежние устройства выходят (API §4.5).</summary>
    public async Task<string> RecoverAsync(string login, string recoveryCode, string newPassword, CancellationToken ct = default)
    {
        var (_, device) = await DeviceForServerAsync(ct).ConfigureAwait(false);
        var session = await Api().RecoverAsync(login, recoveryCode, newPassword, device, ct).ConfigureAwait(false);
        Save(session);
        return session.RecoveryCode ?? "";
    }

    /// <summary>Закончить сессию и на сервере; библиотека остаётся на устройстве.</summary>
    public async Task SignOutAsync()
    {
        var current = _session;
        SetSession(null, new AccountState.SignedOut());
        if (current is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Api(current.ServerUrl).LogoutAsync(current.RefreshToken, timeout.Token).ConfigureAwait(false);
        }
        catch (ApiException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Вызов с действующим access-токеном: обновляется заранее; на <c>access_token_expired</c>/<c>invalid</c> — одно
    /// обновление и один повтор. Сессию, которую закончил сервер, переводит в <see cref="AccountState.AuthRequired"/>.
    /// </summary>
    public async Task<T> AuthorizedAsync<T>(Func<MelogoldApi, string, Task<T>> call, CancellationToken ct = default)
    {
        var session = _session ?? throw new ApiException(401, "unauthorized", "Not signed in");
        var api = Api(session.ServerUrl);
        var token = await FreshTokenAsync(false, ct).ConfigureAwait(false);
        try
        {
            return await call(api, token).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Code is "access_token_expired" or "access_token_invalid")
        {
            token = await FreshTokenAsync(true, ct).ConfigureAwait(false);
            try
            {
                return await call(api, token).ConfigureAwait(false);
            }
            catch (ApiException again) when (again.Code == "session_revoked")
            {
                EndSession();
                throw;
            }
        }
        catch (ApiException e) when (e.Code == "session_revoked")
        {
            EndSession();
            throw;
        }
    }

    public Task AuthorizedAsync(Func<MelogoldApi, string, Task> call, CancellationToken ct = default) =>
        AuthorizedAsync<bool>(async (api, token) =>
        {
            await call(api, token).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Действующий access-токен (для SSE: поток открывается с ним и закрывается сервером в момент <c>exp</c>).</summary>
    public Task<string> AccessTokenAsync(CancellationToken ct = default) => FreshTokenAsync(false, ct);

    private async Task<string> FreshTokenAsync(bool force, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _session ?? throw new ApiException(401, "unauthorized", "Not signed in");
            if (!force && current.AccessTokenExpiresAt - IsoTime.NowMs() > RefreshEarlyMs) return current.AccessToken;
            RefreshResponse refreshed;
            try
            {
                var hwid = Hwid.Compute(_identity.PlatformId, current.ServerId);
                refreshed = await Api(current.ServerUrl).RefreshAsync(current.RefreshToken, new DevicePatch(hwid, ClientVersion: _identity.ClientVersion), ct).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.Status == 401)
            {
                // Любая 401 от /auth/refresh — сессия окончена (API §1.7); сеть и 5xx её не трогают
                EndSession();
                throw;
            }
            // Новый refresh сохраняется до первого использования нового access (API §1.7)
            var updated = current with
            {
                AccessToken = refreshed.Tokens.AccessToken,
                AccessTokenExpiresAt = IsoTime.Parse(refreshed.Tokens.AccessTokenExpiresAt),
                RefreshToken = refreshed.Tokens.RefreshToken,
            };
            _session = updated;
            _store.Save(updated);
            return updated.AccessToken;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public Task<DeviceListResponse> DevicesAsync(CancellationToken ct = default) =>
        AuthorizedAsync((api, token) => api.DevicesAsync(token, ct), ct);

    public Task RevokeAsync(string deviceId, string? password, CancellationToken ct = default) =>
        AuthorizedAsync((api, token) => api.RevokeDeviceAsync(token, deviceId, password, ct), ct);

    public Task<RevokeOthersResponse> RevokeOthersAsync(string? password, CancellationToken ct = default) =>
        AuthorizedAsync((api, token) => api.RevokeOthersAsync(token, password, ct), ct);

    public Task<MeResponse> MeAsync(CancellationToken ct = default) =>
        AuthorizedAsync((api, token) => api.MeAsync(token, ct), ct);

    public async Task<ChangePasswordResponse> ChangePasswordAsync(string? currentPassword, string newPassword, bool signOutOthers, CancellationToken ct = default)
    {
        var result = await AuthorizedAsync((api, token) => api.ChangePasswordAsync(token, currentPassword, newPassword, signOutOthers, ct), ct).ConfigureAwait(false);
        if (_session is { } current)
        {
            var updated = current with
            {
                AccessToken = result.Tokens.AccessToken,
                AccessTokenExpiresAt = IsoTime.Parse(result.Tokens.AccessTokenExpiresAt),
                RefreshToken = result.Tokens.RefreshToken,
            };
            _session = updated;
            _store.Save(updated);
        }
        return result;
    }

    public async Task DeleteAccountAsync(string password, CancellationToken ct = default)
    {
        await AuthorizedAsync((api, token) => api.DeleteAccountAsync(token, password, ct), ct).ConfigureAwait(false);
        SetSession(null, new AccountState.SignedOut());
    }

    /// <summary>Сервер закончил сессию: войти снова, данные остаются.</summary>
    public void EndSession()
    {
        var current = _session;
        if (current is null) return;
        SetSession(null, new AccountState.AuthRequired(current.Login, current.ServerUrl));
    }

    private void Save(AuthSession session)
    {
        var stored = new StoredSession(
            ServerUrl,
            session.ServerId,
            session.User.Id,
            session.User.Login,
            session.Device.Id,
            session.Tokens.AccessToken,
            IsoTime.Parse(session.Tokens.AccessTokenExpiresAt),
            session.Tokens.RefreshToken);
        SetSession(stored, new AccountState.SignedIn(stored.Login, stored.DeviceId, stored.ServerUrl));
    }

    private void SetSession(StoredSession? session, AccountState state)
    {
        _session = session;
        _store.Save(session);
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        lock (_apiLock) _api?.Api.Dispose();
        _refresh.Dispose();
    }
}
