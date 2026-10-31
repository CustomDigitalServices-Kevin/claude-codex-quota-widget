namespace QuotaWidget.App;

public sealed record GpuTelemetry(string Id, string Name, double? UtilizationPercent,
    ulong? DedicatedUsedBytes, ulong? DedicatedTotalBytes, ulong? SharedUsedBytes);

public sealed record SystemTelemetrySnapshot(DateTimeOffset SampledAt, double? CpuPercent,
    ulong? RamUsedBytes, ulong? RamTotalBytes, GpuTelemetry[] Gpus,
    string? CpuIssue = null, string? RamIssue = null, string? GpuIssue = null);

public sealed class SystemTelemetrySource : IDisposable
{
    private readonly WindowsSystemMetrics system = new();
    private PdhGpuMetrics? gpu;
    private string? gpuStartupIssue;
    private bool disposed;

    public SystemTelemetrySource()
    {
        try { gpu = new PdhGpuMetrics(DxgiAdapters.Enumerate()); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { gpuStartupIssue = Issue(exception); }
    }

    public SystemTelemetrySnapshot Sample()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var sampledAt = DateTimeOffset.UtcNow;
        double? cpu = null;
        ulong? ramUsed = null;
        ulong? ramTotal = null;
        string? cpuIssue = null;
        string? ramIssue = null;
        string? gpuIssue = gpuStartupIssue;
        GpuTelemetry[] gpus = [];

        try { cpu = system.SampleCpu(); }
        catch (Exception exception) when (IsNativeFailure(exception)) { cpuIssue = Issue(exception); }
        try { (ramUsed, ramTotal) = WindowsSystemMetrics.SampleMemory(); }
        catch (Exception exception) when (IsNativeFailure(exception)) { ramIssue = Issue(exception); }
        if (gpu is not null)
        {
            try { gpus = gpu.Sample(); gpuIssue = null; }
            catch (Exception exception) when (IsNativeFailure(exception)) { gpuIssue = Issue(exception); }
        }
        return new(sampledAt, cpu, ramUsed, ramTotal, gpus, cpuIssue, ramIssue, gpuIssue);
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        gpu?.Dispose();
        gpu = null;
        GC.SuppressFinalize(this);
    }

    private static bool IsNativeFailure(Exception exception) => exception is System.ComponentModel.Win32Exception
        or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
        or OutOfMemoryException or OverflowException;

    private static string Issue(Exception exception) => exception.GetType().Name;
}
