namespace QuotaWidget.App;

public sealed record ThemePalette(uint Background, uint Surface, uint Text, uint Secondary,
    uint Track, uint Green, uint Orange, uint Red, uint Tile, uint Border, uint Claude, uint Codex, uint Blue)
{
    public static ThemePalette Light { get; } = new(0xEEF3FA, 0xFFFFFF, 0x17283D, 0x4D627B,
        0xDEE7F0, 0x0A644E, 0x8C4E00, 0xAA3047, 0xF4F7FC, 0xC6D4E5, 0x88502A, 0x07655F, 0x275F9F);
    public static ThemePalette Dark { get; } = new(0x0B111D, 0x111F2E, 0xE9F0FA, 0xA1B2C8,
        0x24364A, 0x5DE4BE, 0xFFCA72, 0xFF8A9C, 0x0D1826, 0x30455D, 0xF3BA76, 0x67DFCA, 0x8BBFF5);
    public static ThemePalette Black { get; } = Dark with { Background = 0x050505, Surface = 0x111111,
        Tile = 0x090909, Track = 0x292929, Border = 0x383838, Text = 0xF2F2F2, Secondary = 0xADADAD };
}

public static class ThemePolicy
{
    public static bool FollowsWindows(WidgetSettings settings) => settings.Theme == ThemeChoice.System
        || CardPreferences.Normalize(settings.Cards).Any(card => card.ThemeChoice == ThemeChoice.System);

    public static ThemeChoice Resolve(ThemeChoice choice, object? appsUseLightTheme) => choice switch
    {
        ThemeChoice.Light => ThemeChoice.Light,
        ThemeChoice.Dark => ThemeChoice.Dark,
        ThemeChoice.Black => ThemeChoice.Black,
        _ => appsUseLightTheme is int value && value == 0 ? ThemeChoice.Dark : ThemeChoice.Light,
    };

    public static double Contrast(uint first, uint second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(uint color) => 0.2126 * Channel((color >> 16) & 255)
        + 0.7152 * Channel((color >> 8) & 255) + 0.0722 * Channel(color & 255);

    private static double Channel(uint value)
    {
        var normalized = value / 255d;
        return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }
}
