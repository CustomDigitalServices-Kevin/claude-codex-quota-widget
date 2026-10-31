namespace QuotaWidget.App.Tests;

public sealed class VisualPolicyTests
{
    [Fact]
    public void SystemCardStillTracksWindowsWhenGlobalBackgroundIsManual()
    {
        Assert.True(ThemePolicy.FollowsWindows(new WidgetSettings(Theme: ThemeChoice.Black,
            Cards: [new("claude", Theme: "System")])));
        Assert.False(ThemePolicy.FollowsWindows(new WidgetSettings(Theme: ThemeChoice.Black)));
        Assert.True(ThemePolicy.FollowsWindows(new WidgetSettings(Theme: ThemeChoice.System)));
    }
    [Theory]
    [InlineData(0, 0, false, -0.7071067811865476, 0.7071067811865476)]
    [InlineData(50, 135, false, 0, -1)]
    [InlineData(100, 270, true, 0.7071067811865476, 0.7071067811865476)]
    [InlineData(140, 270, true, 0.7071067811865476, 0.7071067811865476)]
    [InlineData(-10, 0, false, -0.7071067811865476, 0.7071067811865476)]
    public void GaugeHasClockwise270DegreeSweepAndClampsDrawingOnly(double used, double sweep,
        bool large, double endX, double endY)
    {
        var arc = GaugeGeometry.Arc(used);
        Assert.Equal(sweep, arc.SweepDegrees);
        Assert.Equal(large, arc.IsLargeArc);
        Assert.Equal(-Math.Sqrt(0.5), arc.Start.X, 10);
        Assert.Equal(Math.Sqrt(0.5), arc.Start.Y, 10);
        Assert.Equal(endX, arc.End.X, 10);
        Assert.Equal(endY, arc.End.Y, 10);
        Assert.Equal(GaugeGeometry.Point(Math.Clamp(used / 100, 0, 1)), arc.End);
    }

    [Fact]
    public void NonFiniteGaugeValuesDoNotCreateInvalidGeometry()
    {
        Assert.Equal(GaugeGeometry.Arc(0), GaugeGeometry.Arc(double.NaN));
        Assert.Equal(GaugeGeometry.Arc(0), GaugeGeometry.Arc(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(ThemeChoice.System, 0, ThemeChoice.Dark)]
    [InlineData(ThemeChoice.System, 1, ThemeChoice.Light)]
    [InlineData(ThemeChoice.System, null, ThemeChoice.Light)]
    [InlineData(ThemeChoice.System, 7, ThemeChoice.Light)]
    [InlineData(ThemeChoice.Light, 0, ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark, 1, ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Dark, null, ThemeChoice.Dark)]
    public void ResolvesRegistryOrManualTheme(ThemeChoice choice, int? registry, ThemeChoice expected) =>
        Assert.Equal(expected, ThemePolicy.Resolve(choice, registry));

    [Fact]
    public void InvalidRegistryTypeUsesLightFallback() =>
        Assert.Equal(ThemeChoice.Light, ThemePolicy.Resolve(ThemeChoice.System, "0"));

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Black)]
    public void EveryTextColorHasWcagAAContrastOnBothOpaqueSurfaces(ThemeChoice theme)
    {
        var palette = theme == ThemeChoice.Light ? ThemePalette.Light : theme == ThemeChoice.Black ? ThemePalette.Black : ThemePalette.Dark;
        foreach (var text in new[] { palette.Text, palette.Secondary, palette.Green, palette.Orange, palette.Red,
            palette.Claude, palette.Codex, palette.Blue })
        {
            foreach (var background in new[] { palette.Background, palette.Surface, palette.Tile })
            { Assert.True(ThemePolicy.Contrast(text, background) >= 4.5, $"{theme}: #{text:X6} on #{background:X6}"); }
        }
        // The active pin and hovered header buttons use the track surface too.
        Assert.True(ThemePolicy.Contrast(palette.Text, palette.Track) >= 4.5);
        Assert.True(ThemePolicy.Contrast(palette.Green, palette.Track) >= 4.5);
        Assert.True(ThemePolicy.Contrast(palette.Secondary, palette.Track) >= 4.5);
    }

    [Theory]
    [InlineData(DisplayMode.Detailed, 0, false)]
    [InlineData(DisplayMode.Detailed, 1, false)]
    [InlineData(DisplayMode.Detailed, 2, true)]
    [InlineData(DisplayMode.Detailed, 5, true)]
    [InlineData(DisplayMode.Compact, 2, false)]
    [InlineData(DisplayMode.Gauges, 1, true)]
    public void DetailedOverviewSelectsGaugesOnlyForMultipleWindows(DisplayMode mode, int windows, bool expected) =>
        Assert.Equal(expected, CardVisualPolicy.UsesGauges(mode, windows));

    [Theory]
    [InlineData("claude", "ClaudeAccent", "ClaudeBorder")]
    [InlineData("codex", "CodexAccent", "CodexBorder")]
    [InlineData("unknown", "BlueAccent", "NeutralBorder")]
    public void ProviderIdentityHasAnAccentIndependentOfQuotaStatus(string provider, string accent, string border)
    {
        Assert.Equal(accent, CardVisualPolicy.ProviderAccent(provider));
        Assert.Equal(border, CardVisualPolicy.ProviderBorder(provider));
    }

    [Fact]
    public void ContrastFormulaHasKnownBlackWhiteAndEqualColorResults()
    {
        Assert.Equal(21, ThemePolicy.Contrast(0x000000, 0xFFFFFF), 10);
        Assert.Equal(1, ThemePolicy.Contrast(0x172033, 0x172033), 10);
    }

    [Theory]
    [InlineData(DisplayMode.Detailed, 360, 280)]
    [InlineData(DisplayMode.Compact, 480, 220)]
    [InlineData(DisplayMode.Gauges, 360, 300)]
    public void MinimumSizesAreEnforcedPerMode(DisplayMode mode, double width, double height)
    {
        Assert.Equal(new CardSize(width, height), DisplaySizing.Minimum(mode));
        Assert.True(DisplaySizing.IsValid(mode, new(width, height)));
        Assert.True(DisplaySizing.IsValid(mode, DisplaySizing.Default(mode)));
        Assert.False(DisplaySizing.IsValid(mode, new(width - 1, height)));
        Assert.False(DisplaySizing.IsValid(mode, new(width, height - 1)));
        Assert.False(DisplaySizing.IsValid(mode, new(double.NaN, height)));
        Assert.False(DisplaySizing.IsValid(mode, new(width, double.PositiveInfinity)));
    }
}
