using System.Windows;

namespace QuotaWidget.App;

public static class CardWindowState
{
    // Aero Snap and Win+Up maximize any resizable window. A maximized card covers the monitor and
    // has no resize border left to shrink it, so it goes back to its previous bounds at once.
    public static bool MustRestore(WindowState state) => state == WindowState.Maximized;

    // Saving the bounds of a maximized or minimized card would bring that state back at next start.
    public static bool CanPersistBounds(WindowState state) => state == WindowState.Normal;
}
