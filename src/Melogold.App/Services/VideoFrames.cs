using Melogold.Core.Music;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Melogold.App.Services;

/// <summary>
/// Кадр видео YouTube без чёрных полей (<see cref="FrameBars"/>): обложка сингла в кадре 16:9 становится квадратом,
/// превью 4:3 — кадром без полос сверху и снизу. Делается один раз, когда картинка попадает в кэш изображений.
/// </summary>
public static class VideoFrames
{
    /// <summary>Картинка без полей; без полей или не разобрана — те же байты.</summary>
    public static async Task<byte[]> TrimBarsAsync(byte[] bytes)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            if (FrameBars.Content(pixels, width, height) is not { } content) return bytes;

            var cropped = new byte[content.Width * content.Height * 4];
            for (var y = 0; y < content.Height; y++)
                System.Buffer.BlockCopy(pixels, ((content.Y + y) * width + content.X) * 4, cropped, y * content.Width * 4, content.Width * 4);

            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.92, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)content.Width, (uint)content.Height, 96, 96, cropped);
            await encoder.FlushAsync();
            var result = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(result), (uint)result.Length, InputStreamOptions.None);
            return result;
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn("Video frame not trimmed", e);
            return bytes;
        }
    }
}
