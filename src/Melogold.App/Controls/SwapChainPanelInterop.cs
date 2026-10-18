using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// <c>ISwapChainPanelNative::SetSwapChain</c>: отдать <c>SwapChainPanel</c> swap chain Direct3D (WinUI 3 не показывает его без
/// этого вызова). Интерфейс нативный, не WinRT, поэтому вызывается через таблицу методов напрямую.
/// </summary>
internal static class SwapChainPanelInterop
{
    /// <summary>IID <c>ISwapChainPanelNative</c> Windows App SDK (<c>microsoft.ui.xaml.media.dxinterop.h</c>) и системного XAML.</summary>
    private static readonly Guid[] Interfaces =
    [
        new("63aad0b8-7c24-40ff-85a8-640d944cc325"),
        new("f92f19d2-3ade-45a6-a20c-f6f1ea90554b"),
    ];

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr self, IntPtr swapChain);

    public static bool SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        if (unknown == IntPtr.Zero) return false;
        try
        {
            foreach (var iid in Interfaces)
            {
                var id = iid;
                if (Marshal.QueryInterface(unknown, in id, out var native) < 0 || native == IntPtr.Zero) continue;
                try
                {
                    // IUnknown (3 метода), дальше SetSwapChain
                    var table = Marshal.ReadIntPtr(native);
                    var method = Marshal.ReadIntPtr(table, 3 * IntPtr.Size);
                    return Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(method)(native, swapChain) >= 0;
                }
                finally
                {
                    Marshal.Release(native);
                }
            }
            return false;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
