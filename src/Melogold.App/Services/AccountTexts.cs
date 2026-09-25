using Melogold.Core.Domain;
using Melogold.Server;

namespace Melogold.App.Services;

/// <summary>Тексты аккаунта и синхронизации по кодам API §2 (как <c>accountError</c> Android).</summary>
public static class AccountTexts
{
    public static string Error(Exception error) => Loc.Get((error as ApiException)?.Code switch
    {
        "invalid_credentials" => "AccountErrorCredentials",
        "login_throttled" or "rate_limited" or "reauth_throttled" => "AccountErrorThrottled",
        "device_limit_reached" => "AccountErrorDeviceLimit",
        "login_taken" => "AccountErrorLoginTaken",
        "invalid_login_format" => "AccountErrorLoginFormat",
        "password_too_short" => "AccountErrorPasswordShort",
        "password_too_common" or "password_too_weak" or "password_contains_login" or "password_too_long" => "AccountErrorPasswordWeak",
        "registration_closed" => "AccountErrorRegistrationClosed",
        "recent_device_restricted" => "AccountErrorPasswordNeeded",
        "invalid_password" => "AccountErrorInvalidPassword",
        "client_outdated" => "ServerClientOutdated",
        "server_outdated" => "ServerTooOld",
        _ => error is ApiException { IsNetwork: true } ? "AccountErrorNetwork" : "AccountErrorUnknown",
    });

    /// <summary>«Синхронизировано · 2 минуты назад», «Синхронизация…», «Нет связи с сервером»…</summary>
    public static string Status(SyncStatus status) => status switch
    {
        SyncStatus.Syncing => Loc.Get("SyncStatusSyncing"),
        SyncStatus.Failed failed => Loc.Get(failed.Offline ? "SyncStatusOffline" : "SyncStatusFailed"),
        SyncStatus.Idle { LastSyncAt: { } at } => Loc.Format("SyncStatusDoneFormat", Relative(at)),
        _ => Loc.Get("SyncStatusNever"),
    };

    /// <summary>«только что», «5 минут назад», «2 часа назад», «3 дня назад», дальше — дата.</summary>
    public static string Relative(long epochMs)
    {
        var age = TimeSpan.FromMilliseconds(Math.Max(0, IsoTime.NowMs() - epochMs));
        if (age < TimeSpan.FromMinutes(1)) return Loc.Get("JustNow");
        if (age < TimeSpan.FromHours(1)) return Loc.Plural("MinutesAgo", (long)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return Loc.Plural("HoursAgo", (long)age.TotalHours);
        if (age < TimeSpan.FromDays(7)) return Loc.Plural("DaysAgo", (long)age.TotalDays);
        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentUICulture);
    }

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
