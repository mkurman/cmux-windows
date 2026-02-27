using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cmux.Views;

internal static class WindowAppearance
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                var enabled = 1;
                _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));

                var borderColor = DwmColorNone;
                _ = DwmSetWindowAttribute(hwnd, DwmWindowBorderColor, ref borderColor, sizeof(uint));
            }
            catch
            {
                // Best effort: ignore on unsupported systems.
            }
        };
    }
}
