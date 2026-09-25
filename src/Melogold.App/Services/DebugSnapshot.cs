#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Melogold.App.Services;

/// <summary>
/// Только в отладочной сборке: снимок окна изнутри (RenderTargetBitmap) по запросу <c>tools/shot.ps1</c> — файл
/// <c>shot-request</c> с путём PNG в папке данных. Нужен, когда снимок снаружи (PrintWindow) пустой: экран выключен и
/// DWM не рисует окно. Mica под содержимым заменяется цветом фона темы, открытые всплывающие окна (диалоги) — поверх.
/// </summary>
public static class DebugSnapshot
{
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = [];
    private static readonly HashSet<string> Busy = [];

    /// <summary>Снимки окна по запросу-файлу <paramref name="requestName"/> (главное окно — <c>shot-request</c>).</summary>
    public static void Start(FrameworkElement root, string requestName = "shot-request")
    {
        if (Watchers.Remove(requestName, out var old)) old.Dispose();
        var watcher = new FileSystemWatcher(AppPaths.DataDirectory, requestName) { EnableRaisingEvents = true };
        FileSystemEventHandler handler = (_, _) => root.DispatcherQueue.TryEnqueue(async () => await SaveAsync(root, requestName));
        watcher.Created += handler;
        watcher.Changed += handler;
        Watchers[requestName] = watcher;
    }

    private static async Task SaveAsync(FrameworkElement root, string requestName)
    {
        // Created и Changed приходят парой на один запрос
        if (!Busy.Add(requestName)) return;
        try
        {
            await SaveOnceAsync(root, requestName);
        }
        finally
        {
            Busy.Remove(requestName);
        }
    }

    private static async Task SaveOnceAsync(FrameworkElement root, string requestName)
    {
        var request = Path.Combine(AppPaths.DataDirectory, requestName);
        string target;
        try
        {
            target = (await File.ReadAllTextAsync(request)).Trim();
            File.Delete(request);
        }
        catch (IOException)
        {
            return;
        }
        if (target.Length == 0) return;
        if (target.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            // Вместо снимка — дерево раскладки: где элемент, какой ширины и почему
            var lines = new List<string>();
            DumpTree(root, root, 0, lines);
            await File.WriteAllLinesAsync(target, lines);
            return;
        }
        try
        {
            var scale = root.XamlRoot.RasterizationScale;
            var width = (int)Math.Round(root.ActualWidth * scale);
            var height = (int)Math.Round(root.ActualHeight * scale);
            var dark = root.ActualTheme == ElementTheme.Dark;
            byte bg = dark ? (byte)0x20 : (byte)0xF3;
            var canvas = new byte[width * height * 4];
            for (var i = 0; i < canvas.Length; i += 4)
            {
                canvas[i] = canvas[i + 1] = canvas[i + 2] = bg;
                canvas[i + 3] = 255;
            }
            await DrawAsync(canvas, width, height, root, 0, 0);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
            {
                if (popup.Child is not FrameworkElement child || child.ActualWidth <= 0) continue;
                var at = child.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                await DrawAsync(canvas, width, height, child, (int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale));
            }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96 * scale, 96 * scale, canvas);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            await File.WriteAllBytesAsync(target + ".tmp", bytes);
            File.Move(target + ".tmp", target, true);
        }
        catch (Exception e)
        {
            Log.Warn("Debug snapshot failed", e);
        }
    }

    /// <summary>Рисует элемент поверх холста: пиксели RenderTargetBitmap — BGRA с предумноженной альфой.</summary>
    /// <summary>Строка на элемент: тип, имя, x и ширина в окне (DIP), заданные ширины, отступы и выравнивание.</summary>
    private static void DumpTree(DependencyObject node, FrameworkElement root, int depth, List<string> lines)
    {
        if (depth > 60) return;
        if (node is FrameworkElement e)
        {
            if (e.Visibility == Visibility.Collapsed) return;
            var x = e.ActualWidth > 0 ? e.TransformToVisual(root).TransformPoint(default).X : double.NaN;
            static string W(double v) => double.IsNaN(v) ? "-" : double.IsInfinity(v) ? "inf" : v.ToString("0");
            lines.Add($"{new string(' ', depth)}{e.GetType().Name} '{e.Name}' x={W(x)} w={W(e.ActualWidth)} width={W(e.Width)} min={W(e.MinWidth)} max={W(e.MaxWidth)} " +
                $"margin={e.Margin.Left:0},{e.Margin.Right:0} align={e.HorizontalAlignment}");
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) DumpTree(VisualTreeHelper.GetChild(node, i), root, depth + 1, lines);
    }

    private static async Task DrawAsync(byte[] canvas, int width, int height, UIElement element, int left, int top)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        for (var y = 0; y < h; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= height) continue;
            for (var x = 0; x < w; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= width) continue;
                var s = (y * w + x) * 4;
                var d = (cy * width + cx) * 4;
                var alpha = pixels[s + 3];
                if (alpha == 0) continue;
                for (var c = 0; c < 3; c++) canvas[d + c] = (byte)Math.Min(255, pixels[s + c] + canvas[d + c] * (255 - alpha) / 255);
            }
        }
    }
}
#endif
