using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Cmux.Core.Config;

namespace Cmux.Views;

internal static class WindowAppearance
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private sealed class UiScaleState
    {
        public FrameworkElement? RootElement;
        public ScaleTransform? Transform;
        public double BaseWidth;
        public double BaseHeight;
        public double BaseMinWidth;
        public double BaseMinHeight;
        public double CurrentScale = 1.0;
        public bool IsInitialized;
        public bool IsUpdating;
        public Action? SettingsChangedHandler;
    }

    private static readonly Dictionary<Window, UiScaleState> UiScaleStates = [];

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

                // Hook WM_GETMINMAXINFO to prevent maximized window from covering the taskbar
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(MaximizeBoundsHook);
            }
            catch
            {
                // Best effort: ignore on unsupported systems.
            }
        };

        AttachUiScaling(window);
    }

    public static void IncreaseUiScale() => SetUiScalePercent(SettingsService.Current.UiScalePercent + 10);

    public static void DecreaseUiScale() => SetUiScalePercent(SettingsService.Current.UiScalePercent - 10);

    public static void ResetUiScale() => SetUiScalePercent(100);

    private static void SetUiScalePercent(int percent)
    {
        percent = Math.Clamp(percent, 100, 200);

        var settings = SettingsService.Current;
        if (settings.UiScalePercent == percent)
            return;

        settings.UiScalePercent = percent;
        SettingsService.Save(settings);
        SettingsService.NotifyChanged();
    }

    private static double GetUiScaleFactor()
    {
        return Math.Clamp(SettingsService.Current.UiScalePercent, 100, 200) / 100.0;
    }

    private static void AttachUiScaling(Window window)
    {
        if (UiScaleStates.ContainsKey(window))
            return;

        var state = new UiScaleState();
        UiScaleStates[window] = state;

        window.Loaded += (_, _) => ApplyUiScale(window, state, captureBaseMetrics: true);
        window.SizeChanged += (_, _) => UpdateBaseSizeMetrics(window, state);
        window.Closed += (_, _) =>
        {
            if (state.SettingsChangedHandler != null)
                SettingsService.SettingsChanged -= state.SettingsChangedHandler;
            UiScaleStates.Remove(window);
        };

        state.SettingsChangedHandler = () =>
        {
            if (window.Dispatcher.HasShutdownStarted)
                return;

            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!window.IsLoaded)
                    return;

                ApplyUiScale(window, state, captureBaseMetrics: false);
            }));
        };

        SettingsService.SettingsChanged += state.SettingsChangedHandler;
    }

    private static void ApplyUiScale(Window window, UiScaleState state, bool captureBaseMetrics)
    {
        if (window.Content is not FrameworkElement root)
            return;

        if (!state.IsInitialized || !ReferenceEquals(state.RootElement, root))
        {
            state.RootElement = root;
            state.Transform ??= new ScaleTransform(1.0, 1.0);
            root.LayoutTransform = state.Transform;
            captureBaseMetrics = true;
            state.IsInitialized = true;
        }

        if (captureBaseMetrics)
            CaptureBaseMetrics(window, state);

        var scale = GetUiScaleFactor();
        if (Math.Abs(state.CurrentScale - scale) < 0.001 && !captureBaseMetrics)
            return;

        state.IsUpdating = true;
        try
        {
            state.Transform!.ScaleX = scale;
            state.Transform.ScaleY = scale;

            if (state.BaseMinWidth > 0)
                window.MinWidth = state.BaseMinWidth * scale;
            if (state.BaseMinHeight > 0)
                window.MinHeight = state.BaseMinHeight * scale;

            if (window.WindowState == WindowState.Normal)
            {
                if (state.BaseWidth > 0)
                    window.Width = state.BaseWidth * scale;
                if (state.BaseHeight > 0)
                    window.Height = state.BaseHeight * scale;
            }

            state.CurrentScale = scale;
        }
        finally
        {
            state.IsUpdating = false;
        }
    }

    private static void CaptureBaseMetrics(Window window, UiScaleState state)
    {
        var divisor = Math.Max(0.1, state.CurrentScale);
        state.BaseWidth = NormalizeBaseMetric(window.Width, window.ActualWidth, divisor);
        state.BaseHeight = NormalizeBaseMetric(window.Height, window.ActualHeight, divisor);
        state.BaseMinWidth = NormalizeBaseMetric(window.MinWidth, window.MinWidth, divisor);
        state.BaseMinHeight = NormalizeBaseMetric(window.MinHeight, window.MinHeight, divisor);
    }

    private static void UpdateBaseSizeMetrics(Window window, UiScaleState state)
    {
        if (!state.IsInitialized || state.IsUpdating || window.WindowState != WindowState.Normal)
            return;

        var divisor = Math.Max(0.1, state.CurrentScale);
        if (window.ActualWidth > 0)
            state.BaseWidth = window.ActualWidth / divisor;
        if (window.ActualHeight > 0)
            state.BaseHeight = window.ActualHeight / divisor;
    }

    private static double NormalizeBaseMetric(double preferredValue, double fallbackValue, double divisor)
    {
        var value = double.IsNaN(preferredValue) || preferredValue <= 0 ? fallbackValue : preferredValue;
        return value > 0 ? value / divisor : 0;
    }

    private static nint MaximizeBoundsHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != nint.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var work = info.rcWork;
                    var mon = info.rcMonitor;
                    mmi.ptMaxPosition.x = work.left - mon.left;
                    mmi.ptMaxPosition.y = work.top - mon.top;
                    mmi.ptMaxSize.x = work.right - work.left;
                    mmi.ptMaxSize.y = work.bottom - work.top;
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return nint.Zero;
    }
}
