using System.Runtime.InteropServices;
using Melogold.Core.Music;
using Windows.ApplicationModel.DataTransfer;

namespace Melogold.App.Services;

/// <summary>
/// «Поделиться» (GLOSSARY «Меню трека»): системное окно Windows — Telegram, почта, «Обмен с устройствами поблизости».
/// Окну нужен HWND: у классического приложения <c>DataTransferManager</c> берётся через <c>IDataTransferManagerInterop</c>.
/// </summary>
public static class Share
{
    private static readonly Guid DataTransferManagerId = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);
    private static DataTransferManager? _manager;
    private static (string Title, Uri Link)? _pending;

    public static void Track(Track track) =>
        Link(track.Title, new Uri(track.IsVideo ? $"https://www.youtube.com/watch?v={track.VideoId}" : $"https://music.youtube.com/watch?v={track.VideoId}"));

    public static void Link(string title, Uri link)
    {
        if (App.Current?.Window is not { } window) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            if (_manager is null)
            {
                _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, DataTransferManagerId));
                _manager.DataRequested += OnDataRequested;
            }
            _pending = (title, link);
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // Нет окна «Поделиться» (старая Windows, политика) — ссылка в буфер, как «Копировать ссылку»
            Log.Warn("Share UI unavailable", e);
            var package = new DataPackage();
            package.SetText(link.ToString());
            Clipboard.SetContent(package);
            App.Current?.Window?.Snackbar.Show(Loc.Get("LinkCopied"));
        }
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pending is not { } pending) return;
        var data = args.Request.Data;
        data.Properties.Title = pending.Title;
        data.SetWebLink(pending.Link);
        data.SetText(pending.Link.ToString());
    }

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, in Guid riid);

        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
