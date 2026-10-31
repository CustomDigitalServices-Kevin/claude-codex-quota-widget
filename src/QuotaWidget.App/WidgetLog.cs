using System.Globalization;
using System.Text;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public enum WidgetEvent
{
    Started, Stopped, SettingsMissing, SettingsInvalid, SettingsSaved, CollectionCompleted,
    CollectionFailed, PurgeCompleted, PurgeFailed, DispatcherFailure, DomainFailure,
    TaskFailure, StorageFailure, DesktopFailure, AccountObserved, QuotaAlert, ClaudeMeasurementStale, AppearanceFallback,
}

public sealed class WidgetLog(string directory, TimeProvider clock)
{
    public const int MaximumBytes = 1024 * 1024;
    private readonly object gate = new();
    public string FilePath { get; } = Path.Combine(directory, "widget.log");

    // A closed vocabulary prevents file contents, source paths and exception messages from
    // accidentally leaking identifiers or tokens. Exceptions contribute only type and HResult.
    public void Record(WidgetEvent entry, double? value = null, Exception? exception = null) => Write(
        entry.ToString() + (value is { } number ? " " + number.ToString("0.###", CultureInfo.InvariantCulture) : "")
        + (exception is null ? "" : " " + exception.GetType().Name + " " + exception.HResult.ToString(CultureInfo.InvariantCulture)),
        exception is null ? "INFO" : "ERROR");

    public void RecordAccount(string account) => Write("AccountObserved " + JsonSupport.Fingerprint(account), "INFO");

    public void RecordCodexMethod(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        Write("CodexMethod " + new string(method.Where(c => !char.IsControl(c)).Take(128).ToArray()), "INFO");
    }

    private void Write(string message, string level)
    {
        var line = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) + " " + level + " " + message + "\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                {
                    var first = Path.Combine(directory, "widget.1.log");
                    var second = Path.Combine(directory, "widget.2.log");
                    if (File.Exists(first)) { File.Move(first, second, true); }
                    File.Move(FilePath, first, true);
                }
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must not recursively fail inside an unhandled-exception handler.
                System.Diagnostics.Debug.WriteLine("QuotaWidget log unavailable: " + exception.GetType().Name);
            }
        }
    }
}
