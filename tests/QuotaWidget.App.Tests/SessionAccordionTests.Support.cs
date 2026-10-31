using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuotaWidget.App.Tests;

public sealed partial class SessionAccordionTests
{
    private static MainWindow Window(RecentSessionReference[] references,
        Func<RecentSessionReference[], CancellationToken, SessionDetails[]> read)
    {
        var window = new MainWindow { AllowClose = true, ReadSessionDetails = read };
        window.ApplySettings(new WidgetSettings(Cards:
            [new("codex"), new("claude", false), new("pc", false)]));
        window.Render(Presentation);
        window.RenderTelemetry(null, Activity(references));
        return window;
    }

    private static RecentActivitySnapshot Activity(RecentSessionReference[] references) =>
        new(DateTimeOffset.UtcNow, references.Length, false, "fixture", References: references);

    private static RecentSessionReference Reference(string id) =>
        new("codex", id, DateTimeOffset.UtcNow.AddMinutes(-1), null);

    private static Func<RecentSessionReference[], CancellationToken, SessionDetails[]> BlockingRead(
        ManualResetEventSlim entered, ManualResetEventSlim finished) => (items, token) =>
    {
        entered.Set();
        try
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            var item = Assert.Single(items);
            return [new("codex", item.Id, item.ObservedAt, "Late result")];
        }
        finally { finished.Set(); }
    };

    private static T Find<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        Assert.IsType<T>(LogicalDescendants(root).Single(item =>
            AutomationProperties.GetAutomationId(item) == automationId));

    private static T? FindOrDefault<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        LogicalDescendants(root).OfType<T>().SingleOrDefault(item =>
            AutomationProperties.GetAutomationId(item) == automationId);

    private static string[] UiStrings(DependencyObject root)
    {
        var strings = new List<string>();
        foreach (var item in LogicalDescendants(root))
        {
            if (item is TextBlock text) { strings.Add(text.Text); }
            if (item is ContentControl { Content: string content }) { strings.Add(content); }
            if (item is FrameworkElement { ToolTip: string tooltip }) { strings.Add(tooltip); }
            var name = AutomationProperties.GetName(item);
            if (!string.IsNullOrWhiteSpace(name)) { strings.Add(name); }
        }
        return [.. strings];
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in LogicalDescendants(child)) { yield return descendant; }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.True(condition(), "Expected UI state was not reached within one second.");
    }

    private static void RunStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action();
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Session accordion UI test exceeded 30 seconds.");
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }
}
