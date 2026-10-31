using System.IO;

namespace QuotaWidget.App.Tests;

public sealed class TelemetryMonitorTests
{
    [Fact]
    public async Task PublicationFailureCannotPreventCleanShutdown()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.Root, TimeProvider.System);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Deliberate fault injection tests shutdown, not production telemetry values.
        var monitor = new TelemetryMonitor((_, _) => { invoked.TrySetResult(); throw new InvalidOperationException("Injected publication failure"); },
            log, () => new(DateTimeOffset.UtcNow, null, null, null, []), SyntheticActivity);
        monitor.Start();
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
        Assert.Contains("DesktopFailure", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamplingFailurePublishesUnknownValuesAndCanBeCancelled()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.Root, TimeProvider.System);
        var received = new TaskCompletionSource<SystemTelemetrySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new TelemetryMonitor((snapshot, _) => received.TrySetResult(snapshot), log,
            () => throw new InvalidOperationException("Injected sampling failure"), SyntheticActivity);
        monitor.Start();
        var snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(snapshot.CpuPercent);
        Assert.Empty(snapshot.Gpus);
        Assert.NotNull(snapshot.CpuIssue);
    }

    private static RecentActivitySnapshot SyntheticActivity() => new(DateTimeOffset.UtcNow, null, true, "Synthetic lifecycle test.");
}
