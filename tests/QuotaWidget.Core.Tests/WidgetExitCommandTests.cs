using System.Diagnostics;
using QuotaWidget.Cli;

namespace QuotaWidget.Core.Tests;

public sealed class WidgetExitCommandTests
{
    [Fact]
    public void MatchingInstanceReceivesQuitAndOtherDirectoryDoesNot()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new LifetimeFixture();
        var process = fixture.Start("first");
        Assert.Equal(0, WidgetExitCommand.RequestExit(Path.Combine(fixture.Root, "absent"),
            new StringWriter(), fixture.ProcessName, fixture.EventName, 2000));
        Assert.False(process.HasExited);
        Assert.Equal(0, WidgetExitCommand.RequestExit(Path.Combine(fixture.Root, "first"),
            new StringWriter(), fixture.ProcessName, fixture.EventName, 2000));
        Assert.True(process.WaitForExit(2000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void AnotherLocationPreventsSignalingAndTimeoutIsAnError()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new LifetimeFixture();
        var first = fixture.Start("first", ignoreQuit: true);
        var second = fixture.Start("second");
        Assert.Equal(1, WidgetExitCommand.RequestExit(Path.Combine(fixture.Root, "first"),
            new StringWriter(), fixture.ProcessName, fixture.EventName, 100));
        Assert.False(first.HasExited);
        Assert.False(second.HasExited);
        second.Kill();
        Assert.True(second.WaitForExit(2000));
        Assert.Equal(1, WidgetExitCommand.RequestExit(Path.Combine(fixture.Root, "first"),
            new StringWriter(), fixture.ProcessName, fixture.EventName, 100));
        Assert.False(first.HasExited);
    }

    private sealed class LifetimeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "QuotaWidgetLifecycleTests", Guid.NewGuid().ToString("N"));
        public string ProcessName { get; } = "QWTest" + Guid.NewGuid().ToString("N")[..8];
        public string EventName { get; } = @"Local\QuotaWidget.Tests." + Guid.NewGuid().ToString("N");
        private readonly List<Process> processes = [];

        public Process Start(string name, bool ignoreQuit = false)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            var original = Path.Combine(AppContext.BaseDirectory, "fake-codex");
            foreach (var file in Directory.GetFiles(original))
            { File.Copy(file, Path.Combine(path, Path.GetFileName(file))); }
            var executable = Path.Combine(path, ProcessName + ".exe");
            File.Copy(Path.Combine(path, "QuotaWidget.FakeCodex.exe"), executable);
            var ready = Path.Combine(path, "ready.txt");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--widget-lifetime", EventName, ready, ignoreQuit ? "ignore" : "quit" } };
            var process = Process.Start(start)!;
            processes.Add(process);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready) || process.HasExited, 5000));
            Assert.True(File.Exists(ready));
            return process;
        }

        public void Dispose()
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
            GC.SuppressFinalize(this);
        }
    }
}
