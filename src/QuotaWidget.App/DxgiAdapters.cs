using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuotaWidget.App;

public sealed record GpuAdapterDescriptor(string Id, string Name, ulong DedicatedTotalBytes);

internal static class DxgiAdapters
{
    private const uint DxgiErrorNotFound = 0x887A0002;
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");

    internal static GpuAdapterDescriptor[] Enumerate()
    {
        var id = Factory1Id;
        var result = CreateDXGIFactory1(ref id, out var factory);
        if (result < 0) { throw new Win32Exception(result); }
        var adapters = new List<GpuAdapterDescriptor>();
        try
        {
            var enumAdapters = Method<EnumAdapters1Delegate>(factory, 12);
            for (uint index = 0; ; index++)
            {
                var code = unchecked((uint)enumAdapters(factory, index, out var adapter));
                if (code == DxgiErrorNotFound) { break; }
                if ((int)code < 0) { throw new Win32Exception(unchecked((int)code)); }
                try
                {
                    var getDescription = Method<GetDescription1Delegate>(adapter, 10);
                    var descriptionResult = getDescription(adapter, out var description);
                    if (descriptionResult < 0) { throw new Win32Exception(descriptionResult); }
                    if ((description.Flags & 2) != 0) { continue; }
                    adapters.Add(new(Format(description.AdapterLuid),
                        description.Description.TrimEnd('\0', ' '), checked((ulong)description.DedicatedVideoMemory)));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return [.. adapters];
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static string Format(AdapterLuid luid) => $"{unchecked((uint)luid.HighPart):X8}_{luid.LowPart:X8}";

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(ref Guid factoryId, out nint factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(nint factory, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetDescription1Delegate(nint adapter, out AdapterDescription1 description);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid { internal uint LowPart; internal int HighPart; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint VendorId;
        internal uint DeviceId;
        internal uint SubSystemId;
        internal uint Revision;
        internal nuint DedicatedVideoMemory;
        internal nuint DedicatedSystemMemory;
        internal nuint SharedSystemMemory;
        internal AdapterLuid AdapterLuid;
        internal uint Flags;
    }
}
