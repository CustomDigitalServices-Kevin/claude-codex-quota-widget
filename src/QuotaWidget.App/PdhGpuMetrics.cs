using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QuotaWidget.App;

public sealed record GpuCounterValue(string Instance, double Value, uint Status = 0);

internal sealed class PdhGpuMetrics : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint MaximumBufferBytes = 4 * 1024 * 1024;
    private readonly GpuAdapterDescriptor[] adapters;
    private readonly PdhGpuInstanceCache identityCache = new();
    private readonly long initialCollection;
    private nint query;
    private nint engines;
    private nint dedicated;
    private nint shared;
    private bool disposed;
    private bool emittedFirstSample;

    internal PdhGpuMetrics(GpuAdapterDescriptor[] adapters)
    {
        this.adapters = adapters;
        Check(PdhOpenQueryW(null, 0, out query));
        try
        {
            Check(PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out engines));
            Check(PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out dedicated));
            Check(PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Shared Usage", 0, out shared));
            Check(PdhCollectQueryData(query));
            initialCollection = Stopwatch.GetTimestamp();
        }
        catch { Dispose(); throw; }
    }

    internal GpuTelemetry[] Sample()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(PdhCollectQueryData(query));
        var ready = emittedFirstSample && Stopwatch.GetElapsedTime(initialCollection) >= TimeSpan.FromSeconds(1);
        var result = PdhGpuAggregation.Aggregate(adapters, Read(engines), Read(dedicated), Read(shared), ready, identityCache);
        emittedFirstSample = true;
        return result;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        if (query != 0) { _ = PdhCloseQuery(query); query = 0; }
        GC.SuppressFinalize(this);
    }

    private static GpuCounterValue[] Read(nint counter)
    {
        uint bytes = 0;
        var count = 0u;
        var status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out count, 0);
        if (status == 0 && count == 0) { return []; }
        if (status != MoreData) { Check(status); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (bytes == 0 || bytes > MaximumBufferBytes) { throw new InvalidOperationException("PDH counter array size is invalid."); }
            var allocated = bytes;
            var buffer = Marshal.AllocHGlobal(checked((int)allocated));
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out count, buffer);
                if (status == MoreData) { continue; }
                Check(status);
                var size = Marshal.SizeOf<FormattedCounterItem>();
                if (count > allocated / (uint)size) { throw new InvalidOperationException("PDH counter array count is invalid."); }
                var values = new GpuCounterValue[checked((int)count)];
                for (var index = 0; index < values.Length; index++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterItem>(buffer + checked(index * size));
                    values[index] = new(Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue, item.Value.Status);
                }
                return values;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("PDH counter instances changed during collection.");
    }

    private static void Check(uint status)
    {
        if (status != 0) { throw new Win32Exception(unchecked((int)status), "PDH " + status.ToString("X8", CultureInfo.InvariantCulture)); }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes,
        out uint count, nint items);

    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(nint query);

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterItem { internal nint Name; internal FormattedCounterValue Value; }

    [StructLayout(LayoutKind.Explicit)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] internal uint Status;
        [FieldOffset(8)] internal double DoubleValue;
    }
}

public static partial class PdhGpuAggregation
{
    private const uint ValidData = 0;
    private const uint NewData = 1;

    [GeneratedRegex(@"^pid_\d+_luid_0x(?<high>[0-9A-Fa-f]{8})_0x(?<low>[0-9A-Fa-f]{8})_phys_(?<phys>\d+)_eng_(?<engine>\d+)_engtype_.+$", RegexOptions.CultureInvariant)]
    private static partial Regex EnginePattern();

    [GeneratedRegex(@"^luid_0x(?<high>[0-9A-Fa-f]{8})_0x(?<low>[0-9A-Fa-f]{8})_phys_(?<phys>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryPattern();

    public static GpuTelemetry[] Aggregate(IEnumerable<GpuAdapterDescriptor> adapters,
        IEnumerable<GpuCounterValue> engines, IEnumerable<GpuCounterValue> dedicated,
        IEnumerable<GpuCounterValue> shared, bool utilizationReady)
        => Aggregate(adapters, engines, dedicated, shared, utilizationReady, new PdhGpuInstanceCache());

    internal static GpuTelemetry[] Aggregate(IEnumerable<GpuAdapterDescriptor> adapters,
        IEnumerable<GpuCounterValue> engines, IEnumerable<GpuCounterValue> dedicated,
        IEnumerable<GpuCounterValue> shared, bool utilizationReady, PdhGpuInstanceCache identityCache)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(identityCache);
        var engineByAdapter = Valid(engines)
            .Select(value => (Identity: EngineIdentity(value.Instance, identityCache), value.Value))
            .GroupBy(item => item.Identity)
            .Select(group => (group.Key.AdapterId, Value: group.Sum(item => item.Value)))
            .GroupBy(item => item.AdapterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Clamp(group.Max(item => item.Value), 0, 100), StringComparer.OrdinalIgnoreCase);
        var dedicatedByAdapter = MemoryValues(dedicated, identityCache);
        var sharedByAdapter = MemoryValues(shared, identityCache);
        return adapters.Select(adapter => new GpuTelemetry(adapter.Id, adapter.Name,
            utilizationReady && engineByAdapter.TryGetValue(adapter.Id, out var utilization) ? utilization : null,
            dedicatedByAdapter.GetValueOrDefault(adapter.Id), adapter.DedicatedTotalBytes,
            sharedByAdapter.GetValueOrDefault(adapter.Id))).ToArray();
    }

    private static Dictionary<string, ulong?> MemoryValues(IEnumerable<GpuCounterValue> values,
        PdhGpuInstanceCache identityCache)
    {
        return Valid(values).Select(value => (Identity: MemoryIdentity(value.Instance, identityCache), value.Value))
            .GroupBy(item => item.Identity.AdapterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (ulong?)Math.Max(0, group.Sum(item => item.Value)), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<GpuCounterValue> Valid(IEnumerable<GpuCounterValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Where(value => (value.Status is ValidData or NewData) && double.IsFinite(value.Value));
    }

    private static PdhGpuCounterIdentity EngineIdentity(string instance, PdhGpuInstanceCache identityCache)
    {
        if (identityCache.TryGetEngine(instance, out var identity)) { return identity; }
        var match = EnginePattern().Match(instance);
        if (!match.Success) { throw new InvalidOperationException("GPU engine instance format is unsupported."); }
        identity = new(Id(match), match.Groups["phys"].Value, match.Groups["engine"].Value);
        identityCache.Store(instance, identity);
        return identity;
    }

    private static PdhGpuCounterIdentity MemoryIdentity(string instance, PdhGpuInstanceCache identityCache)
    {
        if (identityCache.TryGetMemory(instance, out var identity)) { return identity; }
        var match = MemoryPattern().Match(instance);
        if (!match.Success) { throw new InvalidOperationException("GPU memory instance format is unsupported."); }
        identity = new(Id(match), match.Groups["phys"].Value, null);
        identityCache.Store(instance, identity);
        return identity;
    }

    private static string Id(Match match) => match.Groups["high"].Value.ToUpperInvariant() + "_"
        + match.Groups["low"].Value.ToUpperInvariant();
}

internal readonly record struct PdhGpuCounterIdentity(string AdapterId, string Physical, string? Engine);

internal sealed class PdhGpuInstanceCache
{
    private const int MaximumEntries = 4096;
    private readonly Dictionary<string, PdhGpuCounterIdentity> identities = new(StringComparer.Ordinal);

    internal int Count => identities.Count;

    internal bool TryGetEngine(string instance, out PdhGpuCounterIdentity identity) =>
        identities.TryGetValue(instance, out identity) && identity.Engine is not null;

    internal bool TryGetMemory(string instance, out PdhGpuCounterIdentity identity) =>
        identities.TryGetValue(instance, out identity) && identity.Engine is null;

    internal void Store(string instance, PdhGpuCounterIdentity identity)
    {
        if (identities.Count < MaximumEntries) { identities.TryAdd(instance, identity); }
    }
}
