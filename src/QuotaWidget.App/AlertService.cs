using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed class AlertService(string dataDirectory, WidgetLog log, TimeProvider clock)
{
    private readonly string path = Path.Combine(dataDirectory, "alerts.json");

    // Called only on the serialized collection worker, never on age-only UI refreshes.
    public QuotaAlert[] Process(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var previous = File.Exists(path) ? AtomicJsonFile.Read<AlertState>(path) : null;
            if (File.Exists(path) && (previous is null || previous.Thresholds is null
                || previous.Thresholds.Any(t => t is null || t.Identity is null)))
            { throw new JsonException("Invalid alert state."); }
            var result = AlertPolicy.Evaluate(StateBuilder.Build(snapshot.Observations, snapshot.Blockages), previous, clock.GetUtcNow());
            // Persist before publishing: a restart cannot replay already issued thresholds.
            AtomicJsonFile.Write(path, result.State);
            foreach (var alert in result.Alerts)
            { log.Record(alert.Window is null ? WidgetEvent.ClaudeMeasurementStale : WidgetEvent.QuotaAlert, alert.Threshold); }
            return result.Alerts;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Record(WidgetEvent.StorageFailure, exception: exception);
            return [];
        }
    }
}
