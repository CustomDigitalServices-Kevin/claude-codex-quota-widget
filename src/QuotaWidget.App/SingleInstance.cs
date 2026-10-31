namespace QuotaWidget.App;

public sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle show = new(false, EventResetMode.AutoReset, @"Local\QuotaWidget.Show");
    private readonly EventWaitHandle quit = new(false, EventResetMode.AutoReset, @"Local\QuotaWidget.Quit");
    private readonly Mutex mutex = new(false, @"Local\QuotaWidget.Instance");
    private RegisteredWaitHandle? showWait;
    private RegisteredWaitHandle? quitWait;
    public bool IsOwner { get; }

    public SingleInstance()
    {
        try { IsOwner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
        if (!IsOwner) { show.Set(); }
    }

    public void Listen(Action onShow, Action onQuit)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        ArgumentNullException.ThrowIfNull(onQuit);
        showWait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShow(), null, Timeout.Infinite, false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => onQuit(), null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        showWait?.Unregister(null);
        quitWait?.Unregister(null);
        if (IsOwner) { mutex.ReleaseMutex(); }
        show.Dispose();
        quit.Dispose();
        mutex.Dispose();
    }
}
