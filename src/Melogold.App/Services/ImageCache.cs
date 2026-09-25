using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Melogold.Core.Music;

namespace Melogold.App.Services;

/// <summary>
/// Кэш изображений (Android: кэш Coil и «Максимальный размер»): обложки лежат файлами в <c>cache/images</c> и
/// повторно не качаются. Сверх лимита удаляются те, что дольше всех не показывались.
/// </summary>
public sealed class ImageCache
{
    private readonly SettingsStore _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, Task<string?>> _loading = new();
    private int _sinceTrim;

    public ImageCache(SettingsStore settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.ToolUserAgent);
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsStore.ImageCacheMaxMb)) _ = Task.Run(Trim);
        };
    }

    public static string Directory => Path.Combine(AppPaths.Cache, "images");

    /// <summary>Занято на диске, байт.</summary>
    public long Size => Files().Sum(f => f.Length);

    public long MaxBytes => _settings.ImageCacheMaxMb * 1024L * 1024;

    /// <summary>Файл изображения: из кэша или скачанный сейчас; null — не удалось (тогда картинку грузит сама WinUI).</summary>
    public Task<string?> GetAsync(Uri uri)
    {
        // Кадр видео лежит без чёрных полей (VideoFrames); «#frame» — чтобы не взять файл, сохранённый до обрезки
        var frame = Thumbnails.IsWide(uri.AbsoluteUri);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(frame ? uri.AbsoluteUri + "#frame" : uri.AbsoluteUri)))[..32];
        return _loading.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                return await LoadAsync(uri, Path.Combine(Directory, k), frame).ConfigureAwait(false);
            }
            finally
            {
                _loading.TryRemove(k, out _);
            }
        }));
    }

    private async Task<string?> LoadAsync(Uri uri, string path, bool frame)
    {
        try
        {
            if (File.Exists(path))
            {
                // Показали — значит, нужна: удаление по лимиту начинается с давно не показанных
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return path;
            }
            var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
            if (frame) bytes = await VideoFrames.TrimBarsAsync(bytes).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            // Размер папки считается не на каждую картинку
            if (Interlocked.Increment(ref _sinceTrim) % 50 == 0) Trim();
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Trim()
    {
        try
        {
            var files = Files().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var size = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (size <= MaxBytes) break;
                size -= file.Length;
                file.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Image cache trim failed", e);
        }
    }

    public void Clear()
    {
        foreach (var file in Files())
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private static IEnumerable<FileInfo> Files() =>
        System.IO.Directory.Exists(Directory) ? new DirectoryInfo(Directory).EnumerateFiles() : [];
}
