using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuotaWidget.App;

internal sealed class WindowsSystemMetrics
{
    private CpuTimes? previous;

    internal double? SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) { throw new Win32Exception(); }
        var current = new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        var result = previous is { } prior ? SystemTelemetryMath.CpuPercent(prior, current) : null;
        previous = current;
        return result;
    }

    internal static (ulong Used, ulong Total) SampleMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) { throw new Win32Exception(); }
        return (status.TotalPhysical - status.AvailablePhysical, status.TotalPhysical);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low; internal uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public static class SystemTelemetryMath
{
    public static double? CpuPercent(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) { return null; }
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        if (ulong.MaxValue - kernel < user) { return null; }
        var total = kernel + user;
        if (total == 0 || idle > total) { return null; }
        return Math.Clamp(100d * (total - idle) / total, 0, 100);
    }
}
