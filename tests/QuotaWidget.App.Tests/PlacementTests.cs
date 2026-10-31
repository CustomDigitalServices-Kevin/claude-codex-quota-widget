namespace QuotaWidget.App.Tests;

public sealed class PlacementTests
{
    private static readonly DesktopRect Primary = new(0, 0, 1920, 1040);

    [Fact]
    public void FirstStartCentersOnPrimary() => Assert.Equal(new DesktopPoint(510, 320),
        Placement.Place(new(double.NaN, double.NaN, 900, 400), [Primary], Primary));

    [Fact]
    public void SavedPositionOnSingleScreenIsKept() => Assert.Equal(new DesktopPoint(150, 90),
        Placement.Place(new(150, 90, 900, 400), [Primary], Primary));

    [Fact]
    public void RemovedMonitorRecenters() => Assert.Equal(new DesktopPoint(510, 320),
        Placement.Place(new(2100, 90, 900, 400), [Primary], Primary));

    [Fact]
    public void NegativeCoordinatesOnLeftMonitorRemainValid() => Assert.Equal(new DesktopPoint(-1500, 90),
        Placement.Place(new(-1500, 90, 900, 400), [new(-1920, 0, 1920, 1040), Primary], Primary));

    [Theory]
    [InlineData(1800, 1000, true)]
    [InlineData(1801, 1000, false)]
    [InlineData(1800, 1001, false)]
    public void VisibilityRequires120By40OnOneMonitor(double x, double y, bool visible)
    {
        var result = Placement.Place(new(x, y, 900, 400), [Primary], Primary);
        Assert.Equal(visible ? new(x, y) : new DesktopPoint(510, 320), result);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DpiScalesSizeAndDoesNotScaleNegativeDesktopOrigin(double scale)
    {
        var width = Placement.DipToPixels(600, scale);
        Assert.Equal(600, Placement.PixelsToDip(width, scale));
        var monitor = new DesktopRect(-2560, 0, 2560, 1440);
        Assert.Equal(new DesktopPoint(-2400, 80), Placement.Place(new(-2400, 80, width, Placement.DipToPixels(300, scale)), [monitor, Primary], Primary));
    }

    [Fact]
    public void ExplicitRecenterDiscardsValidPosition() => Assert.Equal(new DesktopPoint(510, 320),
        Placement.Place(new(50, 50, 900, 400), [Primary], Primary, true));
}
