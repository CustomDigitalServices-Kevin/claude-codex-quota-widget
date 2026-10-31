using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace QuotaWidget.App;

internal static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static DesktopRect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect))
        { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static void Place(Window window, WidgetSettings settings, bool recenter)
    {
        var screens = Forms.Screen.AllScreens;
        var primary = screens.FirstOrDefault(s => s.Primary) ?? screens[0];
        var workAreas = screens.Select(WorkArea).ToArray();
        // Moving to another monitor can synchronously change WPF's DPI and content size.
        // Recompute once after layout so centering uses the destination monitor's size.
        for (var pass = 0; pass < 2; pass++)
        {
            window.UpdateLayout();
            var bounds = Bounds(window);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var card = bounds with
            {
                X = settings.X ?? double.NaN, Y = settings.Y ?? double.NaN,
                Width = Placement.DipToPixels(window.ActualWidth, dpi.DpiScaleX),
                Height = Placement.DipToPixels(window.ActualHeight, dpi.DpiScaleY),
            };
            var position = Placement.Place(card, workAreas, WorkArea(primary), recenter);
            if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                    (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, 0x0015))
            { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        }
    }

    private static DesktopRect WorkArea(Forms.Screen screen) =>
        new(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);

    internal static double AvailableHeight(Window window)
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
        return screen.WorkingArea.Height / System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
    }

    internal static double AvailableWidth(Window window)
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
        return screen.WorkingArea.Width / System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
    }

    internal static void KeepFullyVisible(Window window)
    {
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
        var bounds = Bounds(window);
        var x = Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - bounds.Width));
        var y = Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height));
        if (x != bounds.X || y != bounds.Y)
        { _ = SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, (int)x, (int)y, 0, 0, 0x0015); }
    }
}
