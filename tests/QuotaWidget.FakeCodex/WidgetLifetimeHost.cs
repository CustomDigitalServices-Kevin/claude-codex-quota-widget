using System.Runtime.Versioning;

// Explicit process fixture for installer lifecycle tests, never included in release payloads.
internal static class WidgetLifetimeHost
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4
            || !args[1].StartsWith(@"Local\QuotaWidget.Tests.", StringComparison.Ordinal)) { return 64; }
        return Wait(args);
    }

    [SupportedOSPlatform("windows")]
    private static int Wait(string[] args)
    {
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, args[1]);
        File.WriteAllText(args[2], "synthetic lifecycle fixture ready");
        if (args[3] == "ignore") { Thread.Sleep(TimeSpan.FromSeconds(30)); return 0; }
        return quit.WaitOne(TimeSpan.FromSeconds(30)) ? 0 : 65;
    }
}
