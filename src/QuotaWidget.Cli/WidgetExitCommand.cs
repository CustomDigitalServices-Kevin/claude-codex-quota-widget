using System.Diagnostics;
using System.Runtime.Versioning;

namespace QuotaWidget.Cli;

public static class WidgetExitCommand
{
    public static int? Run(string[] args, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] != "--request-widget-exit") { return null; }
        ArgumentNullException.ThrowIfNull(error);
        if (!OperatingSystem.IsWindows() || (args.Length != 1
            && (args.Length != 3 || args[1] != "--target-directory" || !Path.IsPathFullyQualified(args[2]))))
        { error.WriteLine("Invalid Windows lifecycle command."); return 1; }
        return RequestExit(args.Length == 3 ? args[2] : Path.GetDirectoryName(Environment.ProcessPath)!, error);
    }

    [SupportedOSPlatform("windows")]
    public static int RequestExit(string targetDirectory, TextWriter error, string processName = "QuotaWidget",
        string quitEventName = @"Local\QuotaWidget.Quit", int timeoutMilliseconds = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var directory = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var session = Process.GetCurrentProcess().SessionId;
            var sameSession = processes.Where(process => !process.HasExited && process.SessionId == session).ToArray();
            var own = sameSession.Where(process =>
                string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), directory,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (own.Length == 0) { return 0; }
            if (sameSession.Length != own.Length)
            { error.WriteLine("Another widget location is running; close the target widget manually."); return 1; }
            if (!EventWaitHandle.TryOpenExisting(quitEventName, out var quit))
            { error.WriteLine("Close the widget before continuing."); return 1; }
            using (quit) { quit.Set(); }
            foreach (var process in own)
            {
                if (!process.WaitForExit(timeoutMilliseconds))
                { error.WriteLine("Widget is still running; installation was stopped."); return 1; }
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or IOException)
        { error.WriteLine("Unable to verify or stop the installed widget safely."); return 1; }
        finally { foreach (var process in processes) { process.Dispose(); } }
    }
}
