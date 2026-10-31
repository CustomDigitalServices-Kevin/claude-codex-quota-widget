using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class CollectionServiceTests
{
    [Fact]
    public async Task ConcurrentRequestsShareOneBackgroundCollection()
    {
        using var directory = new TestDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var count = 0;
        var writes = 0;
        // Explicit test delegates simulate a slow source; no real profile is read.
        await using var service = new CollectionService(_ =>
        {
            Interlocked.Increment(ref count);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); }
            return TestData.Snapshot;
        }, _ => writes++, () => { }, new(directory.Root, new TestClock(TestData.Now)), new TestClock(TestData.Now));
        var first = service.RefreshAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Same(first, service.RefreshAsync());
        }
        finally { release.Set(); }
        await first;
        Assert.Equal(1, count);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task FailureKeepsPreviousSnapshotAndLogsDuration()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.Root, new TestClock(TestData.Now));
        var calls = 0;
        await using var service = new CollectionService(previous =>
        {
            Assert.Same(TestData.Snapshot, previous);
            calls++;
            throw new IOException("test failure");
        }, _ => throw new InvalidOperationException("No write expected"), () => { }, log, new TestClock(TestData.Now), TestData.Snapshot);
        await service.RefreshAsync();
        Assert.Equal(1, calls);
        Assert.Same(TestData.Snapshot, service.Latest);
        Assert.Contains("CollectionFailed", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgesInitiallyAndAfterSixHoursAndRetainsValueOnWriteFailure()
    {
        using var directory = new TestDirectory();
        var clock = new TestClock(TestData.Now);
        var purges = 0;
        var failWrite = false;
        await using var service = new CollectionService(_ => TestData.Snapshot with { WrittenAt = clock.Now },
            _ => { if (failWrite) { throw new IOException("test write failure"); } }, () => purges++, new(directory.Root, clock), clock);
        await service.RefreshAsync();
        Assert.Equal(1, purges);
        clock.Now = clock.Now.AddHours(5);
        await service.RefreshAsync();
        Assert.Equal(1, purges);
        var previous = service.Latest;
        clock.Now = clock.Now.AddHours(1);
        failWrite = true;
        await service.RefreshAsync();
        Assert.Equal(2, purges);
        Assert.Same(previous, service.Latest);
    }
}
