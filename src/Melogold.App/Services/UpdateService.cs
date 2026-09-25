using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Melogold.App.Services;

/// <summary>Файл установщика одной архитектуры в <c>update.json</c>.</summary>
public sealed record UpdateAsset(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// <c>update.json</c> релиза (docs/PROMPT.md §3): формат Android (<c>version</c>, <c>notes</c> с ключами <c>ru</c> и
/// <c>en</c>, <c>publishedAt</c>) плюс установщики по архитектурам в <c>assets</c>.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("notes")] Dictionary<string, string>? Notes,
    [property: JsonPropertyName("publishedAt")] string? PublishedAt,
    [property: JsonPropertyName("assets")] Dictionary<string, UpdateAsset>? Assets)
{
    public string? LocalizedNotes => Notes is null ? null
        : Notes.TryGetValue(Loc.IsRussian ? "ru" : "en", out var notes) ? notes : Notes.Values.FirstOrDefault();
}

public enum UpdateState
{
    /// <summary>Отладочная сборка: сама не обновляется.</summary>
    Disabled,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,

    /// <summary>Нет связи с GitHub или файл не прошёл проверку.</summary>
    Failed,
}

/// <summary>
/// Обновления из GitHub Releases (docs/PROMPT.md §3, как <c>AppUpdater.kt</c> Android и Clementine):
/// <c>releases/latest/download/update.json</c> без лимитов API; проверка при старте не чаще раза в 6 часов и по
/// кнопке; загрузка с прогрессом → проверка размера и SHA-256 → тихий установщик (<c>/VERYSILENT /SUPPRESSMSGBOXES
/// /NORESTART /RELAUNCH</c>) → приложение закрывается, установщик запускает его снова.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string ManifestUrl = "https://github.com/melogold-app/melogoldWindows/releases/latest/download/update.json";
    private const string DownloadBase = "https://github.com/melogold-app/melogoldWindows/releases/download";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly HttpClient _http;

    public UpdateService(SettingsStore settings)
    {
        _settings = settings;
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.ToolUserAgent);
#if DEBUG
        State = UpdateState.Disabled;
#endif
    }

    [ObservableProperty]
    public partial UpdateState State { get; set; } = UpdateState.Idle;

    [ObservableProperty]
    public partial UpdateManifest? Available { get; set; }

    /// <summary>Скачано, %.</summary>
    [ObservableProperty]
    public partial int Progress { get; set; }

    /// <summary>Есть обновление — <c>InfoBadge</c> на пункте «Настройки».</summary>
    public bool HasUpdate => Available is not null;

    partial void OnAvailableChanged(UpdateManifest? value) => OnPropertyChanged(nameof(HasUpdate));

    /// <summary>Проверка нашла версию новее этой: окно «Вышла новая версия» или уведомление Windows.</summary>
    public event Action<UpdateManifest>? Found;

    private DispatcherQueueTimer? _timer;

    /// <summary>
    /// Проверки без нажатия: при каждом запуске (через 5 с — окно и воспроизведение важнее) и потом раз в 6 часов, пока
    /// Melogold открыт.
    /// </summary>
    public void Start()
    {
        if (State == UpdateState.Disabled || _timer is not null) return;
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (timer, _) =>
        {
            timer.Interval = CheckInterval;
            _ = CheckAsync(force: true);
        };
        _timer.Start();
    }

    /// <summary>Проверка; без <paramref name="force"/> — не чаще раза в 6 часов.</summary>
    public async Task CheckAsync(bool force)
    {
        if (State is UpdateState.Disabled or UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && now - _settings.LastUpdateCheck < CheckInterval.TotalMilliseconds) return;
        Set(UpdateState.Checking);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var manifest = await _http.GetFromJsonAsync<UpdateManifest>(ManifestUrl, timeout.Token).ConfigureAwait(false);
            _settings.LastUpdateCheck = now;
            var newer = manifest is not null && IsNewer(manifest.Version, AppInfo.Version) && Asset(manifest) is not null;
            _dispatcher.TryEnqueue(() =>
            {
                Available = newer ? manifest : null;
                State = newer ? UpdateState.Available : UpdateState.UpToDate;
                if (newer) Found?.Invoke(manifest!);
            });
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Релизов с update.json ещё нет: обновляться не на что
            _settings.LastUpdateCheck = now;
            Set(UpdateState.UpToDate);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            Log.Warn("Update check failed", e);
            Set(UpdateState.Failed);
        }
    }

    /// <summary>Скачать, проверить и запустить установщик; приложение закрывается само.</summary>
    public async Task InstallAsync()
    {
        if (Available is not { } manifest || Asset(manifest) is not { } asset || State is UpdateState.Downloading or UpdateState.Installing) return;
        Set(UpdateState.Downloading);
        Progress = 0;
        try
        {
            var path = await DownloadAsync(manifest, asset).ConfigureAwait(false);
            Set(UpdateState.Installing);
            Log.Info($"Installing update {manifest.Version}");
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH") { UseShellExecute = true });
            // Установщик ждёт, пока Melogold закроется, и запускает его снова
            _dispatcher.TryEnqueue(() => Microsoft.UI.Xaml.Application.Current.Exit());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Update install failed", e);
            // Available остаётся: карточка предложит «Обновить» ещё раз
            Set(UpdateState.Failed);
        }
    }

    private async Task<string> DownloadAsync(UpdateManifest manifest, UpdateAsset asset)
    {
        Directory.CreateDirectory(AppPaths.Updates);
        var target = Path.Combine(AppPaths.Updates, Path.GetFileName(asset.FileName));
        // Уже скачанный и целый файл не качается заново
        if (File.Exists(target) && new FileInfo(target).Length == asset.SizeBytes && await HashAsync(target).ConfigureAwait(false) == asset.Sha256.ToLowerInvariant()) return target;

        var url = $"{DownloadBase}/v{manifest.Version}/{Uri.EscapeDataString(asset.FileName)}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var temp = target + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                done += read;
                var percent = (int)(done * 100 / Math.Max(1, asset.SizeBytes));
                _dispatcher.TryEnqueue(() => Progress = Math.Min(100, percent));
            }
        }
        // Размер и SHA-256 — до запуска: битый или подменённый файл не ставится
        if (new FileInfo(temp).Length != asset.SizeBytes) throw new InvalidDataException("Update size mismatch");
        if (await HashAsync(temp).ConfigureAwait(false) != asset.Sha256.ToLowerInvariant()) throw new InvalidDataException("Update SHA-256 mismatch");
        File.Move(temp, target, true);
        return target;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    /// <summary>Установщик своей архитектуры: <c>x64</c> или <c>arm64</c>.</summary>
    public static UpdateAsset? Asset(UpdateManifest manifest) => manifest.Assets?.GetValueOrDefault(AppInfo.Architecture);

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    /// <summary>«вышла 25 сентября» для карточки.</summary>
    public static string? PublishedText(UpdateManifest manifest) =>
        DateTimeOffset.TryParse(manifest.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentUICulture) : null;

    private void Set(UpdateState state) => _dispatcher.TryEnqueue(() => State = state);
}
