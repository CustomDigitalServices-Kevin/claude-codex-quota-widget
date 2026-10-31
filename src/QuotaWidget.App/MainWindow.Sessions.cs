using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuotaWidget.App;

public partial class MainWindow
{
    private readonly Dictionary<string, SessionAccordionState> sessionAccordions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> sessionToggles = new(StringComparer.Ordinal);
    private bool sessionsClosed;
    internal Func<RecentSessionReference[], CancellationToken, SessionDetails[]> ReadSessionDetails { get; set; } =
        (items, token) => new SessionDetailsSource().Read(items, token);

    internal RecentSessionReference[] GetRecentSessions(string provider) =>
        lastActivity?.References?.Where(reference => reference.Provider == provider).ToArray() ?? [];

    private SessionAccordionState SessionState(string provider)
    {
        if (!sessionAccordions.TryGetValue(provider, out var state))
        { state = new(); sessionAccordions.Add(provider, state); }
        return state;
    }

    internal async Task ToggleSessionDetailsAsync(string provider)
    {
        if (sessionsClosed) { return; }
        var state = SessionState(provider);
        if (state.Expanded)
        {
            CollapseSessionDetails(state);
            RefreshSessionArea(provider);
            return;
        }
        if (!Preference(provider).Visible || GetRecentSessions(provider).Length == 0) { return; }
        state.Expanded = true;
        await ReloadSessionDetailsAsync(provider);
    }

    internal async Task ReloadSessionDetailsAsync(string provider)
    {
        var state = SessionState(provider);
        if (sessionsClosed || !state.Expanded || !Preference(provider).Visible) { return; }
        state.Pending?.Cancel();
        var generation = ++state.Generation;
        using var cancellation = new CancellationTokenSource();
        state.Pending = cancellation;
        state.References = GetRecentSessions(provider);
        var items = state.References;
        state.Details = state.Details.Where(detail => items.Any(item => item.Id == detail.Id)).ToArray();
        state.Loading = true;
        state.Failed = false;
        RefreshSessionArea(provider);
        try
        {
            var details = await Task.Run(() => ReadSessionDetails(items, cancellation.Token), cancellation.Token);
            if (sessionsClosed || !state.Expanded || state.Generation != generation || cancellation.IsCancellationRequested) { return; }
            state.Details = details.Where(detail => detail.Provider == provider
                && items.Any(item => item.Id == detail.Id)).ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (state.Generation == generation) { state.Failed = true; state.Details = []; }
        }
        finally
        {
            if (state.Generation == generation)
            {
                state.Pending = null;
                state.Loading = false;
                if (!sessionsClosed && state.Expanded) { RefreshSessionArea(provider); }
            }
        }
    }

    private Button SessionDetailsButton(string provider)
    {
        var state = SessionState(provider);
        var button = new Button
        {
            Content = state.Expanded ? "−" : "+", Width = 28, Height = 28, FontSize = 20,
            FontFamily = new FontFamily("Segoe UI"), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("IconButton"),
        };
        button.SetResourceReference(Control.ForegroundProperty, CardVisualPolicy.ProviderAccent(provider));
        var name = (state.Expanded ? "Replier" : "Déplier") + " les sessions " + (provider == "claude" ? "Claude" : "Codex");
        button.ToolTip = name;
        AutomationProperties.SetAutomationId(button, provider + "-session-details");
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetItemStatus(button, state.Expanded ? "Déplié" : "Replié");
        button.Click += async (_, _) => await ToggleSessionDetailsAsync(provider);
        sessionToggles[provider] = button;
        return button;
    }

    private void RefreshSessionArea(string provider)
    {
        var restoreFocus = Keyboard.FocusedElement is Button focused
            && AutomationProperties.GetAutomationId(focused) == provider + "-session-details";
        if (activityHosts.TryGetValue(provider, out var host))
        {
            host.Child = ActivityStrip(provider, lastActivity);
            if (restoreFocus && sessionToggles.TryGetValue(provider, out var button)) { button.Focus(); }
        }
        forceFitRequested = true;
        FitToContent();
    }

    private void SynchronizeSessionReferences()
    {
        foreach (var (provider, state) in sessionAccordions.ToArray())
        {
            if (!state.Expanded) { continue; }
            var current = GetRecentSessions(provider);
            if (current.Length == 0)
            {
                CollapseSessionDetails(state);
                RefreshSessionArea(provider);
            }
            else if (!state.References.SequenceEqual(current)) { _ = ReloadSessionDetailsAsync(provider); }
        }
    }

    private void CloseUnavailableSessionDetails()
    {
        foreach (var (provider, state) in sessionAccordions)
        { if (!activityHosts.ContainsKey(provider)) { CollapseSessionDetails(state); } }
    }

    private static void CollapseSessionDetails(SessionAccordionState state)
    {
        state.Expanded = false;
        state.Generation++;
        state.Pending?.Cancel();
        state.Pending = null;
        state.Loading = false;
        state.Details = [];
        state.References = [];
    }

    private void CloseSessionDetails()
    {
        sessionsClosed = true;
        foreach (var state in sessionAccordions.Values) { CollapseSessionDetails(state); }
    }

    private sealed class SessionAccordionState
    {
        internal bool Expanded { get; set; }
        internal bool Loading { get; set; }
        internal bool Failed { get; set; }
        internal int Generation { get; set; }
        internal CancellationTokenSource? Pending { get; set; }
        internal RecentSessionReference[] References { get; set; } = [];
        internal SessionDetails[] Details { get; set; } = [];
    }
}
