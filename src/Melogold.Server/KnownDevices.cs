using System.Text.Json;
using Melogold.Core.Data;

namespace Melogold.Server;

/// <summary>
/// Устройства аккаунта для фильтра «Чьи прослушивания» в Истории и Итогах (tasks/0018). Последний полученный список
/// хранится в базе по сеансу аккаунта и берётся, пока свежего нет: без сети фильтр не пропадает. <c>devices.updated</c>
/// сбрасывает список — экраны перечитывают его (<see cref="Changed"/>).
/// </summary>
public sealed class KnownDevices
{
    private readonly AccountService _account;
    private readonly Library _library;
    private (string Key, IReadOnlyList<DeviceDto> Devices)? _fresh;

    public KnownDevices(AccountService account, Library library, LibrarySync sync)
    {
        _account = account;
        _library = library;
        sync.DevicesChanged += () =>
        {
            _fresh = null;
            Changed?.Invoke();
        };
    }

    /// <summary>Устройство добавили, удалили или переименовали.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default)
    {
        if (_account.Session is not { } session) return [];
        var key = $"account_devices:{session.ServerId}:{session.UserId}";
        if (_fresh is { } fresh && fresh.Key == key) return fresh.Devices;
        try
        {
            var devices = (await _account.DevicesAsync(ct).ConfigureAwait(false)).Devices;
            _fresh = (key, devices);
            _library.SetState(key, JsonSerializer.Serialize(devices, MelogoldApi.Json));
            return devices;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            try
            {
                return _library.GetState(key) is { } json ? JsonSerializer.Deserialize<List<DeviceDto>>(json, MelogoldApi.Json) ?? [] : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Другие устройства фильтра: чьи прослушивания есть здесь и которые есть в аккаунте. Удалённое из аккаунта не
    /// показывается вовсе (решение пользователя 2026-09-30), событие без <c>deviceId</c> — тоже: оба видны только в
    /// «Все устройства».
    /// </summary>
    public static IReadOnlyList<DeviceDto> Others(IEnumerable<string> historyDeviceIds, string? currentDeviceId, IReadOnlyList<DeviceDto> accountDevices)
    {
        var present = historyDeviceIds.Where(id => id.Length > 0 && id != currentDeviceId).ToHashSet(StringComparer.Ordinal);
        return accountDevices.Where(d => present.Contains(d.Id)).ToList();
    }
}
