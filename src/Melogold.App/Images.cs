using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melogold.App;

/// <summary>
/// Обложки для <c>x:Bind</c>: адрес → картинка нужного размера. Строку напрямую в <c>Image.Source</c> не отдаём —
/// пустой адрес там даёт <c>ArgumentException</c>.
/// </summary>
public static class Images
{
    public static ImageSource? From(string? url) => From(url, 0);

    public static ImageSource? From(string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var image = new BitmapImage(uri);
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        return image;
    }

    public static ImageSource? Row(string? url) => From(url, 80);

    public static ImageSource? Card(string? url) => From(url, 320);

    public static ImageSource? Player(string? url) => From(url, 112);
}
