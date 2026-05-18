using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EditeIa.Desktop;

/// <summary>Barra de título del sistema en tema oscuro (Windows 10/11).</summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
            return;

        EnableImmersiveDarkMode(hwnd);

        // Windows 11: color de fondo y texto del caption (#12121B / blanco)
        var caption = ColorRef(0x12, 0x12, 0x1B);
        var text = ColorRef(0xFF, 0xFF, 0xFF);
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }

    private static void EnableImmersiveDarkMode(nint hwnd)
    {
        var useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
    }

    private static int ColorRef(byte r, byte g, byte b) => (b << 16) | (g << 8) | r;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
