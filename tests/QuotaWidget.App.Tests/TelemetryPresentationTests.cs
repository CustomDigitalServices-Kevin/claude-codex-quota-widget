namespace QuotaWidget.App.Tests;

public sealed class TelemetryPresentationTests
{
    [Fact]
    public void GpuAndVramReferToTheSameBusiestAdapterAndTooltipIncludesOthers()
    {
        var metrics = TelemetryPresentation.Format(new(DateTimeOffset.UtcNow, 50, 8UL << 30, 32UL << 30,
            [new("integrated", "Integrated GPU", 40, 1UL << 30, 2UL << 30, 2UL << 30),
                new("dedicated", "Dedicated GPU", 20, 4UL << 30, 16UL << 30, 1UL << 30)]));
        Assert.Equal("50 %", metrics[0].Value);
        Assert.Equal("25 %", metrics[1].Value);
        Assert.Equal("Integrated GPU", metrics[2].Detail);
        Assert.Equal("1,0 Gio", metrics[3].Value);
        Assert.Equal("sur 2,0 Gio", metrics[3].Detail);
        Assert.Contains("Dedicated GPU", metrics[2].ToolTip, StringComparison.Ordinal);
        Assert.Contains("partagée", metrics[3].ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMetricsAreNeverPresentedAsZero()
    {
        Assert.All(TelemetryPresentation.Format(null), metric => Assert.Equal("…", metric.Value));
        Assert.All(TelemetryPresentation.Format(new(DateTimeOffset.UtcNow, null, null, null, [])),
            metric => Assert.Equal("Indisponible", metric.Value));
        Assert.Equal("Indisponible", TelemetryPresentation.Percent(double.NaN));
        Assert.Equal("Indisponible", TelemetryPresentation.Percent(double.PositiveInfinity));
    }
}
