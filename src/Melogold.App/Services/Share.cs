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
    private static (string Title, Action<DataPackage> Fill)? _pending;

    public static void Track(Track track)
    {
        var shown = ViewModels.RowVm.Display?.Invoke(track) ?? track;
        Link(shown.Title, shown.ArtistsText, Melogold.Core.Domain.ShareLinks.Track(track));
    }

    /// <summary>Ссылка и текст «Название — исполнитель» над ней (tasks/0016): так её видит тот, кому отправили.</summary>
    public static void Link(string title, string? subtitle, string url) => Show(title, data =>
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var link)) data.SetWebLink(link);
        data.SetText(Melogold.Core.Domain.ShareLinks.Message(title, subtitle, url));
        if (!string.IsNullOrWhiteSpace(subtitle)) data.Properties.Description = subtitle;
    }, () =>
    {
        // Нет окна «Поделиться» (старая Windows, политика) — ссылка в буфер, как «Скопировать ссылку»
        CopyLink(url);
    });

    /// <summary>«Скопировать ссылку»: только ссылка, «Ссылка скопирована».</summary>
    public static void CopyLink(string url)
    {
        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
        App.Current?.Window?.Snackbar.Show(Loc.Get("LinkCopied"));
    }

    /// <summary>Файл — картинка «Итогов года» (tasks/0015); без окна «Поделиться» — <paramref name="fallback"/>.</summary>
    public static void File(string title, Windows.Storage.StorageFile file, Action fallback) => Show(title, data =>
    {
        data.SetStorageItems([file]);
        data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
    }, fallback);

    private static void Show(string title, Action<DataPackage> fill, Action fallback)
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
            _pending = (title, fill);
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Warn("Share UI unavailable", e);
            fallback();
        }
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pending is not { } pending) return;
        var data = args.Request.Data;
        data.Properties.Title = pending.Title;
        pending.Fill(data);
    }

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, in Guid riid);

        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
