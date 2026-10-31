using System.Text.Json.Serialization;

namespace QuotaWidget.App;

[JsonConverter(typeof(JsonStringEnumConverter<DisplayMode>))]
public enum DisplayMode { Detailed, Compact, Gauges }

[JsonConverter(typeof(JsonStringEnumConverter<ThemeChoice>))]
public enum ThemeChoice { System, Light, Dark, Black }

public sealed record CardSize(double Width, double Height);

public sealed record ModeSizes(CardSize? Detailed = null, CardSize? Compact = null, CardSize? Gauges = null)
{
    public CardSize For(DisplayMode mode) => mode switch
    {
        DisplayMode.Detailed => Detailed ?? DisplaySizing.Default(mode),
        DisplayMode.Compact => Compact ?? DisplaySizing.Default(mode),
        DisplayMode.Gauges => Gauges ?? DisplaySizing.Default(mode),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public ModeSizes With(DisplayMode mode, CardSize size) => mode switch
    {
        DisplayMode.Detailed => this with { Detailed = size },
        DisplayMode.Compact => this with { Compact = size },
        DisplayMode.Gauges => this with { Gauges = size },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    [JsonIgnore]
    public bool IsValid => Enum.GetValues<DisplayMode>().All(mode => DisplaySizing.IsValid(mode, For(mode)));
}

public static class DisplaySizing
{
    public static CardSize Minimum(DisplayMode mode) => mode switch
    {
        DisplayMode.Detailed => new(360, 280),
        DisplayMode.Compact => new(480, 220),
        DisplayMode.Gauges => new(360, 300),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static CardSize Default(DisplayMode mode) => mode switch
    {
        DisplayMode.Detailed => new(460, 780),
        DisplayMode.Compact => new(620, 560),
        DisplayMode.Gauges => new(520, 880),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool IsValid(DisplayMode mode, CardSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        var minimum = Minimum(mode);
        return double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && size.Width >= minimum.Width && size.Height >= minimum.Height
            && size.Width <= 7680 && size.Height <= 4320;
    }
}
