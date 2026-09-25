using Melogold.App.Services;
using Melogold.Core.Music;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melogold.App;

/// <summary>
/// Обложки для <c>x:Bind</c>: адрес → картинка нужного размера. Строку напрямую в <c>Image.Source</c> не отдаём —
/// пустой адрес там даёт <c>ArgumentException</c>. Файл берётся из кэша изображений (<see cref="ImageCache"/>);
/// не вышло — картинку по адресу грузит сама WinUI.
/// </summary>
public static class Images
{
    /// <summary>Кэш изображений; null — до запуска приложения (дизайнер, тесты).</summary>
    public static ImageCache? Cache { get; set; }

    public static ImageSource? From(string? url) => From(url, 0);

    public static ImageSource? From(string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var image = new BitmapImage();
        if (decodeWidth > 0)
        {
            // Кадр видео 16:9 в квадрате режется по бокам: чётким должен быть короткий край — высота
            if (Thumbnails.IsWide(url)) image.DecodePixelHeight = decodeWidth;
            else image.DecodePixelWidth = decodeWidth;
        }
        if (Cache is { } cache && uri.Scheme is "http" or "https") _ = LoadAsync(image, uri, cache);
        else image.UriSource = uri;
        return image;
    }

    private static async Task LoadAsync(BitmapImage image, Uri uri, ImageCache cache)
    {
        try
        {
            if (await cache.GetAsync(uri) is { } path)
            {
                using var stream = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.Read);
                await image.SetSourceAsync(stream);
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Битый файл кэша: картинку загрузит WinUI
        }
        image.UriSource = uri;
    }

    public static ImageSource? Row(string? url) => From(url, 80);

    public static ImageSource? Card(string? url) => From(url, 320);

    public static ImageSource? Player(string? url) => From(url, 112);
}
