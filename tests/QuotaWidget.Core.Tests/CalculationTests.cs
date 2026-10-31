using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class CalculationTests
{
    [Fact]
    public void ResetClustersInclude120SecondsButDoNotChainIndefinitely()
    {
        var first = TestData.Window(used: 70);
        var second = first with { UsedPercent = 30, ResetsAt = first.ResetsAt?.AddSeconds(120), MeasuredAt = TestData.Now.AddMinutes(1) };
        var third = first with { ResetsAt = first.ResetsAt?.AddSeconds(121), UsedPercent = 10 };
        var windows = WindowAggregator.Aggregate([third, first, second]);
        Assert.Equal(2, windows.Length);
        Assert.Equal(30, windows[0].UsedPercent);
        Assert.Equal(10, windows[1].UsedPercent);
    }

    [Fact]
    public void SelectionUsesMeasurementThenRenderingThenPercentWithoutMonotonicMaximum()
    {
        var first = TestData.Window(used: 90);
        var second = first with { UsedPercent = 20, MeasuredAt = TestData.Now.AddSeconds(1) };
        var undated = first with { UsedPercent = 100, MeasuredAt = null, ObservedAt = TestData.Now.AddDays(1) };
        Assert.Equal(second, Assert.Single(WindowAggregator.Aggregate([first, second, undated])));
        var rendered = second with { UsedPercent = 10, ObservedAt = second.ObservedAt.AddSeconds(1) };
        Assert.Equal(rendered, Assert.Single(WindowAggregator.Aggregate([second, rendered])));
        var higher = rendered with { UsedPercent = 11 };
        Assert.Equal(higher, Assert.Single(WindowAggregator.Aggregate([higher, rendered])));
    }

    [Fact]
    public void CountersProvidersAndUnknownResetsAreSeparate()
    {
        var first = TestData.Window("codex");
        Assert.Equal(4, WindowAggregator.Aggregate([first, first with { Counter = "other" },
            first with { ResetsAt = null }, TestData.Window()]).Length);
    }

    [Theory]
    [InlineData(59.99, 0.05, "vert")]
    [InlineData(60, 0.05, "orange")]
    [InlineData(84.99, 0.05, "orange")]
    [InlineData(85, 0.05, "rouge")]
    [InlineData(44.99, 0.5, "vert")]
    [InlineData(45, 0.5, "orange")]
    [InlineData(55, 0.5, "orange")]
    [InlineData(55.01, 0.5, "rouge")]
    [InlineData(90, 0.99, "rouge")]
    [InlineData(9.01, 0.1, "orange")]
    public void ColorsUseUnroundedPercentAndProjection(double used, double elapsed, string color)
    {
        var metrics = QuotaCalculations.Calculate(TestData.Window(used: used, remaining: 1 - elapsed), TestData.Now);
        Assert.Equal(color, metrics.Color);
        Assert.Equal(used - 100 * elapsed, metrics.PaceDifference.GetValueOrDefault(), 6);
    }

    [Fact]
    public void UnknownsStayNullAndMissingDurationUsesRawColor()
    {
        var window = TestData.Window(used: 60) with { DurationMinutes = null, MeasuredAt = null };
        var metrics = QuotaCalculations.Calculate(window, TestData.Now);
        Assert.Null(metrics.ElapsedFraction);
        Assert.Null(metrics.PaceDifference);
        Assert.Null(metrics.AgeSeconds);
        Assert.Equal("orange", metrics.Color);
        Assert.Null(QuotaCalculations.Calculate(window with { UsedPercent = null }, TestData.Now).Color);
    }
}
