using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuotaWidget.App;

internal static class NativeAppearance
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static bool Apply(Window window, ThemeChoice theme, WidgetLog log)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) { return false; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        { log.Record(WidgetEvent.AppearanceFallback, Environment.OSVersion.Version.Build); return false; }
        var corners = Set(handle, 33, 2); // DWMWCP_ROUND
        if (corners < 0) { log.Record(WidgetEvent.AppearanceFallback, corners); return false; }
        var dark = Set(handle, 20, theme is ThemeChoice.Dark or ThemeChoice.Black ? 1 : 0);
        if (dark < 0) { log.Record(WidgetEvent.AppearanceFallback, dark); return false; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        { log.Record(WidgetEvent.AppearanceFallback, Environment.OSVersion.Version.Build); return false; }
        var backdrop = Set(handle, 38, 2); // DWMSBT_MAINWINDOW (Mica)
        if (backdrop < 0)
        { log.Record(WidgetEvent.AppearanceFallback, backdrop); return false; }
        // WindowChrome extends the glass frame again on resize/DPI changes. Opaque section and
        // header surfaces preserve text contrast; gaps between them expose the system backdrop.
        WindowChrome.GetWindowChrome(window).GlassFrameThickness = new Thickness(-1);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        { target.BackgroundColor = Colors.Transparent; }
        return true;
    }

    private static int Set(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
}
