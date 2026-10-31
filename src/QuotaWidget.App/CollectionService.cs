using System.Diagnostics;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed class CollectionService(
    Func<Snapshot?, Snapshot> collect, Action<Snapshot> write, Action purge,
    WidgetLog log, TimeProvider clock, Snapshot? initial = null,
    Func<Snapshot?, bool, CancellationToken, Task<Snapshot>>? collectAsync = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private Task current = Task.CompletedTask;
    private Task? loop;
    private DateTimeOffset? lastPurge;
    private bool directRequested;
    public Snapshot? Latest { get; private set; } = initial;
    public event EventHandler? Updated;

    public void Start()
    {
        lock (gate) { loop ??= RunAsync(); }
    }

    public Task RefreshAsync() => RefreshAsync(false);

    public Task RefreshDirectAsync() => RefreshAsync(true);

    private Task RefreshAsync(bool direct)
    {
        lock (gate)
        {
            if (stopping.IsCancellationRequested) { return Task.CompletedTask; }
            directRequested |= direct;
            if (!current.IsCompleted) { return current; }
            current = Task.Run(CollectAsync);
            return current;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task CollectAsync()
    {
        bool direct;
        lock (gate) { direct = directRequested; directRequested = false; }
        var watch = Stopwatch.StartNew();
        var now = clock.GetUtcNow();
        if (lastPurge is null || now - lastPurge >= TimeSpan.FromHours(6))
        {
            lastPurge = now;
            try { purge(); log.Record(WidgetEvent.PurgeCompleted); }
            catch (Exception exception) { log.Record(WidgetEvent.PurgeFailed, exception: exception); }
        }
        try
        {
            var snapshot = collectAsync is null ? collect(Latest)
                : await collectAsync(Latest, direct, stopping.Token).ConfigureAwait(false);
            write(snapshot);
            Latest = snapshot;
            Updated?.Invoke(this, EventArgs.Empty);
            log.Record(WidgetEvent.CollectionCompleted, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception)
        {
            // Keep the previous snapshot and its original measurement timestamps on failure.
            log.Record(WidgetEvent.CollectionFailed, watch.Elapsed.TotalMilliseconds, exception);
        }
        // A menu request during an automatic pass is serialized on the collection worker.
        bool pending;
        lock (gate) { pending = directRequested && !stopping.IsCancellationRequested; }
        if (pending) { await CollectAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        Task pending;
        lock (gate) { pending = current; }
        if (loop is not null) { await loop.ConfigureAwait(false); }
        await pending.ConfigureAwait(false);
        stopping.Dispose();
        GC.SuppressFinalize(this);
    }
}
