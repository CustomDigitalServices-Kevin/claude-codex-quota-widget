namespace QuotaWidget.App.Tests;

public sealed class SystemTelemetryTests
{
    [Fact]
    public void CpuUsesKernelIncludingIdleWithoutCoreCountDivision()
    {
        var percent = SystemTelemetryMath.CpuPercent(new(100, 200, 100), new(130, 260, 140));
        Assert.Equal(70, percent);
        Assert.Null(SystemTelemetryMath.CpuPercent(new(100, 200, 100), new(99, 260, 140)));
        Assert.Null(SystemTelemetryMath.CpuPercent(new(100, 200, 100), new(100, 200, 100)));
    }

    [Fact]
    public void GpuSumsProcessesPerEngineThenUsesBusiestEngine()
    {
        var adapter = new GpuAdapterDescriptor("00000000_0000002A", "Test GPU", 16UL << 30);
        var engines = new[]
        {
            Engine(1, 2, "3D", 12.5), Engine(2, 2, "3D", 17.5),
            Engine(3, 7, "Copy", 44), Engine(4, 8, "Compute", 8),
        };
        var result = Assert.Single(PdhGpuAggregation.Aggregate([adapter], engines,
            [Memory(2UL << 30)], [Memory(512UL << 20)], true));
        Assert.Equal(44, result.UtilizationPercent);
        Assert.Equal(2UL << 30, result.DedicatedUsedBytes);
        Assert.Equal(16UL << 30, result.DedicatedTotalBytes);
        Assert.Equal(512UL << 20, result.SharedUsedBytes);
    }

    [Fact]
    public void FirstGpuSampleAndInvalidCounterStatusesRemainUnknown()
    {
        var adapter = new GpuAdapterDescriptor("00000000_0000002A", "Test GPU", 8UL << 30);
        var result = Assert.Single(PdhGpuAggregation.Aggregate([adapter], [Engine(1, 2, "3D", 50)],
            [Memory(100, 0xC0000BC6)], [Memory(200, 0xC0000BC6)], false));
        Assert.Null(result.UtilizationPercent);
        Assert.Null(result.DedicatedUsedBytes);
        Assert.Null(result.SharedUsedBytes);
    }

    [Fact]
    public void UnsupportedGpuInstanceGrammarIsReportedInsteadOfInvented()
    {
        var adapter = new GpuAdapterDescriptor("00000000_0000002A", "Test GPU", 1);
        Assert.Throws<InvalidOperationException>(() => PdhGpuAggregation.Aggregate([adapter],
            [new("unexpected", 10)], [], [], true));
        Assert.Throws<InvalidOperationException>(() => PdhGpuAggregation.Aggregate([adapter], [],
            [new("unexpected", 10)], [], true));
    }

    [Fact]
    public void IdentityCachePreservesChangedValuesAndStatusesWithLowerSteadyStateAllocations()
    {
        var adapter = new GpuAdapterDescriptor("00000000_0000002A", "Test GPU", 8UL << 30);
        var firstEngines = Enumerable.Range(0, 1024)
            .Select(index => Engine(index + 1, index, "3D", 1))
            .ToArray();
        var firstMemory = Memory(100);
        var cache = new PdhGpuInstanceCache();

        _ = PdhGpuAggregation.Aggregate([adapter], [firstEngines[0]], [firstMemory], [], true);
        _ = PdhGpuAggregation.Aggregate([adapter], firstEngines, [firstMemory], [firstMemory], true, cache);

        var changedEngines = firstEngines.Select((counter, index) => new GpuCounterValue(
            new string(counter.Instance.AsSpan()), index % 80 + 0.5,
            index % 11 == 0 ? 0xC0000BC6u : index % 2 == 0 ? 0u : 1u)).ToArray();
        var changedDedicated = new GpuCounterValue(new string(firstMemory.Instance.AsSpan()), 999, 0xC0000BC6u);
        var changedShared = new GpuCounterValue(new string(firstMemory.Instance.AsSpan()), 200, 1);

        var cachedStart = GC.GetAllocatedBytesForCurrentThread();
        var cached = PdhGpuAggregation.Aggregate([adapter], changedEngines,
            [changedDedicated], [changedShared], true, cache);
        var cachedBytes = GC.GetAllocatedBytesForCurrentThread() - cachedStart;

        var freshStart = GC.GetAllocatedBytesForCurrentThread();
        var fresh = PdhGpuAggregation.Aggregate([adapter], changedEngines,
            [changedDedicated], [changedShared], true);
        var freshBytes = GC.GetAllocatedBytesForCurrentThread() - freshStart;

        Assert.Equal(fresh, cached);
        var gpu = Assert.Single(cached);
        Assert.Equal(79.5, gpu.UtilizationPercent);
        Assert.Null(gpu.DedicatedUsedBytes);
        Assert.Equal(200UL, gpu.SharedUsedBytes);
        Assert.True(cachedBytes < freshBytes,
            $"Expected cached allocation below fresh parsing: cached={cachedBytes}, fresh={freshBytes}.");
    }

    [Fact]
    public void SaturatedIdentityCacheStillParsesAndMeasuresNewInstancesWithoutGrowing()
    {
        var adapter = new GpuAdapterDescriptor("00000000_0000002A", "Test GPU", 1);
        var engines = Enumerable.Range(0, 4097)
            .Select(index => Engine(index + 1, index, "3D", 1))
            .ToArray();
        var cache = new PdhGpuInstanceCache();

        _ = PdhGpuAggregation.Aggregate([adapter], engines, [], [], true, cache);
        Assert.Equal(4096, cache.Count);

        var uncachedAtCapacity = Engine(4097, 4096, "3D", 12.5);
        var result = Assert.Single(PdhGpuAggregation.Aggregate([adapter], [uncachedAtCapacity],
            [], [], true, cache));
        Assert.Equal(12.5, result.UtilizationPercent);
        Assert.Equal(4096, cache.Count);
    }

    [Fact]
    public void NativeSourceProvidesRamAndRejectsSamplingAfterDispose()
    {
        var source = new SystemTelemetrySource();
        var snapshot = source.Sample();
        Assert.True(snapshot.RamTotalBytes is > 0);
        Assert.InRange(snapshot.RamUsedBytes!.Value, 1UL, snapshot.RamTotalBytes.Value);
        Assert.Null(snapshot.CpuPercent);
        source.Dispose();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(source.Sample);
    }

    private static GpuCounterValue Engine(int process, int engine, string type, double value) =>
        new($"pid_{process}_luid_0x00000000_0x0000002A_phys_0_eng_{engine}_engtype_{type}", value);

    private static GpuCounterValue Memory(double value, uint status = 0) =>
        new("luid_0x00000000_0x0000002A_phys_0", value, status);
}
