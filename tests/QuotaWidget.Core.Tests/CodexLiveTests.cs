using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using QuotaWidget.Core;
using Xunit.Abstractions;

namespace QuotaWidget.Core.Tests;

[Collection("Fake Codex environment")]
public sealed class CodexLiveTests(ITestOutputHelper output)
{
    private static readonly string[] AllowedSequence = ["initialize", "initialized", "account/rateLimits/read"];
    [Fact]
    public async Task TenDedicatedProfileReadsLeaveNoCodexDescendantsAndConsumeNoCredits()
    {
        if (Environment.GetEnvironmentVariable("QUOTAWIDGET_LIVE_TESTS") != "1")
        {
            output.WriteLine("Real Codex test not run: QUOTAWIDGET_LIVE_TESTS=1 requires an authenticated dedicated test profile.");
            return;
        }
        Assert.True(OperatingSystem.IsWindows(), "This live check targets the native Windows executable.");
        List<string> sent = [];
        // Only this opt-in test disables the interval. Lock, authentication, whitelist and timeout remain enabled.
        var source = new CodexActiveSource(new(DefaultPaths.DataDirectory) { DisableIntervalForTests = true }, TimeProvider.System, sent.Add);
        using var stop = new CancellationTokenSource();
        var monitor = new DescendantMonitor();
        var monitoring = monitor.RunAsync(stop.Token);
        List<Observation> readings = [];
        try
        {
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var result = await source.ReadAsync(resumeAuthentication: cycle == 0);
                Assert.Null(result.Diagnostic);
                Assert.NotEmpty(result.Observations);
                var observation = result.Observations.First(o => o.Credits is not null && o.ResetCredits is not null);
                Assert.NotNull(observation.Credits);
                Assert.NotNull(observation.ResetCredits?.AvailableCount);
                readings.Add(observation);
                monitor.Capture();
            }
        }
        finally
        {
            await stop.CancelAsync();
            await monitoring;
        }
        Assert.Equal(Enumerable.Range(0, 10).SelectMany(_ => AllowedSequence), sent);
        Assert.All(readings, reading =>
        {
            Assert.Equal(readings[0].Credits, reading.Credits);
            Assert.Equal(readings[0].ResetCredits?.AvailableCount, reading.ResetCredits?.AvailableCount);
        });
        Assert.Empty(monitor.RemainingCodex());
        output.WriteLine("10 cycles completed: whitelist respected, credits/reset counts unchanged, no surviving Codex descendants.");
    }

    // Track descendants while parents still exist, so reparenting after app-server exits cannot hide a survivor.
    private sealed class DescendantMonitor
    {
        private readonly object gate = new();
        private readonly HashSet<uint> known = [(uint)Environment.ProcessId];

        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Capture();
                    await Task.Delay(20, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public void Capture()
        {
            lock (gate) { Discover(ReadProcesses()); }
        }

        public uint[] RemainingCodex()
        {
            lock (gate)
            {
                var processes = ReadProcesses();
                Discover(processes);
                return processes.Where(p => known.Contains(p.Id) && p.Name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Id).ToArray();
            }
        }

        private void Discover(ProcessRow[] processes)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var process in processes)
                { if (known.Contains(process.Parent)) { changed |= known.Add(process.Id); } }
            } while (changed);
        }

        private static ProcessRow[] ReadProcesses()
        {
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            List<ProcessRow> rows = [];
            if (!Process32First(snapshot, ref entry)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            do { rows.Add(new(entry.ProcessId, entry.ParentProcessId, entry.Executable)); }
            while (Process32Next(snapshot, ref entry));
            return [.. rows];
        }

        private sealed record ProcessRow(uint Id, uint Parent, string Name);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Executable;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    }
}
