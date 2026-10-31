namespace QuotaWidget.App;

internal sealed record WidgetMenuItem(string Label, Action? Execute = null, bool? Checked = null,
    WidgetMenuItem[]? Children = null);

internal static class WidgetMenu
{
    public static string ModeLabel(DisplayMode mode) => mode switch
    { DisplayMode.Detailed => "Détaillé", DisplayMode.Compact => "Compact", DisplayMode.Gauges => "Jauges", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };

    public static string ThemeLabel(ThemeChoice theme) => theme switch
    { ThemeChoice.System => "Système", ThemeChoice.Light => "Blanc", ThemeChoice.Dark => "Bleu nuit", ThemeChoice.Black => "Noir", _ => throw new ArgumentOutOfRangeException(nameof(theme)) };
}
