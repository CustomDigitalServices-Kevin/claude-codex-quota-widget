using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class DisplaySettingsTests
{
    [Theory]
    [InlineData(DisplayMode.Detailed, ThemeChoice.System)]
    [InlineData(DisplayMode.Compact, ThemeChoice.Light)]
    [InlineData(DisplayMode.Gauges, ThemeChoice.Dark)]
    public void ModeThemeAndIndependentSizesRoundTrip(DisplayMode mode, ThemeChoice theme)
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        var store = new SettingsStore(path, new(directory.Root, new TestClock(TestData.Now)));
        var sizes = new ModeSizes(new(480, 530), new(630, 290), new(570, 650));
        var expected = new WidgetSettings(-1200, 50, false, true, mode, theme, sizes);
        store.Save(expected);
        var actual = store.Load();
        Assert.Equal(expected, actual);
        Assert.Equal(sizes.For(mode), actual.Size);
        Assert.Contains("\"mode\": \"" + mode + "\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("is_valid", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void OldFileKeepsPositionVisibilityAndTopmostWithNewDefaults()
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        File.WriteAllText(path, """{"x":-1300.5,"y":42,"topmost":false,"visible":false}""");
        var store = new SettingsStore(path, new(directory.Root, new TestClock(TestData.Now)));
        var settings = store.Load();
        Assert.Equal(new WidgetSettings(-1300.5, 42, false, false), settings);
        Assert.Equal(DisplayMode.Detailed, settings.Mode);
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.Equal(DisplaySizing.Default(DisplayMode.Detailed), settings.Size);
    }

    [Theory]
    [InlineData("""{"mode":"invalid"}""")]
    [InlineData("""{"mode":99}""")]
    [InlineData("""{"theme":"invalid"}""")]
    [InlineData("""{"theme":-1}""")]
    [InlineData("""{"sizes":{"detailed":{"width":0,"height":500}}}""")]
    [InlineData("""{"sizes":{"compact":{"width":479,"height":220}}}""")]
    [InlineData("""{"sizes":{"gauges":{"width":500,"height":299}}}""")]
    [InlineData("""{"sizes":{"gauges":{"width":8000,"height":500}}}""")]
    [InlineData("""{"sizes":{"compact":{"height":250}}}""")]
    [InlineData("""{"sizes":{"detailed":{"width":"NaN","height":500}}}""")]
    [InlineData("""{"sizes":{"detailed":{"width":500,"height":1e999}}}""")]
    public void InvalidNewSettingsUseLoggedDefaults(string json)
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        var log = new WidgetLog(directory.Root, new TestClock(TestData.Now));
        File.WriteAllText(path, json);
        Assert.Equal(new WidgetSettings(), new SettingsStore(path, log).Load());
        Assert.Contains("SettingsInvalid", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatingOneModePreservesOtherSizesAndPlacementUsesSelectedSize()
    {
        var sizes = new ModeSizes().With(DisplayMode.Compact, new(700, 260));
        sizes = sizes.With(DisplayMode.Detailed, new(500, 600));
        var settings = new WidgetSettings(Mode: DisplayMode.Compact, Sizes: sizes);
        Assert.Equal(new CardSize(700, 260), settings.Size);
        Assert.Equal(new CardSize(500, 600), sizes.For(DisplayMode.Detailed));
        Assert.Equal(DisplaySizing.Default(DisplayMode.Gauges), sizes.For(DisplayMode.Gauges));
        var screen = new DesktopRect(0, 0, 1920, 1040);
        Assert.Equal(new DesktopPoint(610, 390), Placement.Place(new(double.NaN, double.NaN,
            settings.Size.Width, settings.Size.Height), [screen], screen));
    }

    [Theory]
    [InlineData(System.Windows.WindowState.Normal, false, true)]
    [InlineData(System.Windows.WindowState.Maximized, true, false)]
    [InlineData(System.Windows.WindowState.Minimized, false, false)]
    public void MaximizedCardIsRestoredAndOnlyNormalBoundsArePersisted(System.Windows.WindowState state,
        bool restore, bool persist)
    {
        Assert.Equal(restore, CardWindowState.MustRestore(state));
        Assert.Equal(persist, CardWindowState.CanPersistBounds(state));
    }
}
