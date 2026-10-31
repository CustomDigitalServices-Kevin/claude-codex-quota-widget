namespace QuotaWidget.App;

internal sealed class TelemetryMonitor(Action<SystemTelemetrySnapshot, RecentActivitySnapshot> publish, WidgetLog log,
    Func<SystemTelemetrySnapshot>? sampleOverride = null, Func<RecentActivitySnapshot>? activityOverride = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;
    private bool disposed;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worker is not null) { return; }
        worker = Task.Run(async () =>
        {
            try
            {
                using var source = sampleOverride is null ? new SystemTelemetrySource() : null;
                var activitySource = new RecentActivitySource();
                RecentActivitySnapshot? activity = null;
                var cycles = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
                do
                {
                    SystemTelemetrySnapshot snapshot;
                    try { snapshot = sampleOverride is not null ? sampleOverride() : source!.Sample(); }
                    catch (Exception exception)
                    {
                        log.Record(WidgetEvent.DesktopFailure, exception: exception);
                        snapshot = new(DateTimeOffset.UtcNow, null, null, null, [], "Relevé indisponible", "Relevé indisponible", "Relevé indisponible");
                    }
                    if (activity is null || cycles++ % 4 == 0) { activity = activityOverride is not null ? activityOverride() : activitySource.Sample(); }
                    if (!stopping.IsCancellationRequested) { publish(snapshot, activity); }
                }
                while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception exception)
            {
                log.Record(WidgetEvent.DesktopFailure, exception: exception);
                try
                {
                    if (!stopping.IsCancellationRequested)
                    {
                        publish(new(DateTimeOffset.UtcNow, null, null, null, [], "Relevé indisponible", "Relevé indisponible", "Relevé indisponible"),
                            new(DateTimeOffset.UtcNow, null, true, "Relevé interrompu, activité indisponible."));
                    }
                }
                catch (Exception publicationFailure) { log.Record(WidgetEvent.DesktopFailure, exception: publicationFailure); }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        await stopping.CancelAsync().ConfigureAwait(false);
        if (worker is not null) { await worker.ConfigureAwait(false); }
        stopping.Dispose();
    }
}
