using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuerySpace;

internal static class WindowTitleBar
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int ParkerBlueColorRef = 0x00AB5C00;
    private const int WhiteColorRef = 0x00FFFFFF;

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var captionColor = ParkerBlueColorRef;
            var textColor = WhiteColorRef;
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
