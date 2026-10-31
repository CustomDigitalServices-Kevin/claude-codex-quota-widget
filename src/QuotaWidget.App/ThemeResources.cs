using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuotaWidget.App;

internal static class ThemeResources
{
    internal static ResourceDictionary Light { get; } = Create(ThemePalette.Light);
    internal static ResourceDictionary Dark { get; } = Create(ThemePalette.Dark);
    internal static ResourceDictionary Black { get; } = Create(ThemePalette.Black);

    internal static ResourceDictionary For(ThemeChoice choice) => choice switch
    { ThemeChoice.Dark => Dark, ThemeChoice.Black => Black, _ => Light };

    internal static ThemeChoice Resolve(ThemeChoice choice, WidgetLog log)
    {
        if (choice != ThemeChoice.System) { return ThemePolicy.Resolve(choice, null); }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return ThemePolicy.Resolve(choice, key?.GetValue("AppsUseLightTheme"));
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Record(WidgetEvent.DesktopFailure, exception: exception);
            return ThemeChoice.Light;
        }
    }

    private static ResourceDictionary Create(ThemePalette palette) => new()
    {
        ["CardBackground"] = Brush(palette.Background), ["SectionSurface"] = Brush(palette.Surface),
        ["PrimaryText"] = Brush(palette.Text), ["SecondaryText"] = Brush(palette.Secondary),
        ["BarTrack"] = Brush(palette.Track), ["StateGreen"] = Brush(palette.Green),
        ["StateOrange"] = Brush(palette.Orange), ["StateRed"] = Brush(palette.Red),
        ["TileSurface"] = Brush(palette.Tile), ["SurfaceBorder"] = Brush(palette.Border),
        ["ClaudeAccent"] = Brush(palette.Claude), ["CodexAccent"] = Brush(palette.Codex),
        ["BlueAccent"] = Brush(palette.Blue),
        ["CardChrome"] = Gradient(palette.Surface, palette.Background),
        ["FrameBorder"] = Gradient(palette.Border, palette.Background),
        ["ClaudeBorder"] = Gradient(palette.Claude, palette.Border, 130),
        ["CodexBorder"] = Gradient(palette.Codex, palette.Border, 130),
        ["NeutralBorder"] = Brush(palette.Border),
    };

    private static SolidColorBrush Brush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(uint first, uint second, byte alpha = 255)
    {
        var start = Color.FromArgb(alpha, (byte)(first >> 16), (byte)(first >> 8), (byte)first);
        var end = Color.FromRgb((byte)(second >> 16), (byte)(second >> 8), (byte)second);
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(0.8, 1));
        brush.Freeze();
        return brush;
    }
}
