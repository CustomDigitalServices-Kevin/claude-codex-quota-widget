using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class App : Application, IDisposable
{
    private readonly TimeProvider clock = TimeProvider.System;
    private SingleInstance? instance;
    private WidgetLog? log;
    private SettingsStore? settingsStore;
    private WidgetSettings settings = new();
    private CollectionService? collector;
    private MainWindow? card;
    private TrayIcon? tray;
    private DispatcherTimer? ageTimer;
    private bool quitting;
    private bool disposed;
    private AlertService? alerts;
    private string? lastConnectionIssue;
    private bool directReadRequested;
    private TelemetryMonitor? telemetry;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (PackagingSmoke.TryRun(this, e.Args)) { return; }
        instance = new SingleInstance();
        if (!instance.IsOwner) { Shutdown(); return; }
        log = new(Path.Combine(DefaultPaths.DataDirectory, "logs"), clock);
        DispatcherUnhandledException += OnDispatcherFailure;
        AppDomain.CurrentDomain.UnhandledException += OnDomainFailure;
        TaskScheduler.UnobservedTaskException += OnTaskFailure;
        log.Record(WidgetEvent.Started);
        settingsStore = new(Path.Combine(DefaultPaths.DataDirectory, "settings.json"), log);
        settings = settingsStore.Load();
        var path = Path.Combine(DefaultPaths.DataDirectory, "snapshot.json");
        var initial = SnapshotStore.Read(path, new List<Diagnostic>());
        var active = new CodexActiveSource(new(DefaultPaths.DataDirectory), clock,
            log.RecordCodexMethod, log.RecordCodexMethod);
        var source = new QuotaCollector(clock, active);
        var firstActivePass = true;
        alerts = new(DefaultPaths.DataDirectory, log, clock);
        collector = new(previous => source.Collect(DefaultPaths.ClaudeDirectory, DefaultPaths.CodexProfiles, previous),
            snapshot => SnapshotStore.Write(path, snapshot),
            () => new ClaudeSource(clock).Purge(DefaultPaths.ClaudeDirectory), log, clock, initial,
            async (previous, direct, token) =>
            {
                var resume = direct || firstActivePass;
                firstActivePass = false;
                // Pick up successful CLI reads before the next passive collection replaces the snapshot.
                var shared = SnapshotStore.Read(path, new List<Diagnostic>());
                if (shared is not null && (previous is null || shared.WrittenAt >= previous.WrittenAt)) { previous = shared; }
                return await source.CollectAsync(DefaultPaths.ClaudeDirectory, DefaultPaths.CodexProfiles,
                    previous, resume, token).ConfigureAwait(false);
            });
        collector.Updated += OnUpdated;
        card = new MainWindow { Topmost = settings.Topmost };
        card.ApplySettings(settings);
        card.ApplyTheme(settings.Theme, log);
        card.MenuItems = MenuItems;
        MainWindow = card;
        card.HideRequested += (_, _) => SetVisible(false);
        card.PositionChanged += (_, _) => SavePosition();
        card.RefreshRequested += (_, _) => _ = collector.RefreshAsync();
        card.PinRequested += (_, _) => ToggleTopmost();
        card.SettingsRequested += (_, _) => OpenSettings();
        card.Loaded += (_, _) => Revalidate(false);
        tray = new(ToggleVisible, MenuItems);
        Render();
        card.RenderTelemetry(null);
        telemetry = new((snapshot, activity) => Post(() => { if (!quitting) { card?.RenderTelemetry(snapshot, activity); } }), log);
        telemetry.Start();
        card.Show();
        if (!settings.Visible) { card.Hide(); }
        ageTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        ageTimer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        instance.Listen(() => Post(() => SetVisible(true)), () => Post(() => _ = QuitAsync()));
        collector.Start();
    }

    private void Post(Action action)
    {
        if (!Dispatcher.HasShutdownStarted) { Dispatcher.BeginInvoke(action); }
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        var emitted = collector?.Latest is { } snapshot ? alerts?.Process(snapshot) ?? [] : [];
        var connectionIssue = collector?.Latest?.Diagnostics.FirstOrDefault(d =>
            d.Code is "profil_dedie_non_connecte" or "authentification_refusee");
        Post(() =>
        {
            Render();
            foreach (var alert in emitted) { tray?.ShowBalloon(alert.Text()); }
            if (!directReadRequested && connectionIssue is not null && connectionIssue.Code != lastConnectionIssue)
            { tray?.ShowBalloon(ActiveDiagnostics.Format(connectionIssue, clock.GetUtcNow())); }
            lastConnectionIssue = connectionIssue?.Code;
        });
    }

    private async Task ReadDirectAsync()
    {
        if (collector is null || quitting || directReadRequested) { return; }
        directReadRequested = true;
        try
        {
            await collector.RefreshDirectAsync();
            var issue = collector.Latest?.Diagnostics.FirstOrDefault(d => d.Source == "app-server"
                && d.Code != "derniere_mesure_conservee" && d.Code != WindowAggregator.ReplacedDiagnosticCode);
            lastConnectionIssue = issue?.Code is "profil_dedie_non_connecte" or "authentification_refusee" ? issue.Code : null;
            tray?.ShowBalloon(issue is null ? "Lecture Codex directe terminée."
                : ActiveDiagnostics.Format(issue, clock.GetUtcNow()));
        }
        finally { directReadRequested = false; }
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && collector is not null) { _ = collector.RefreshAsync(); }
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Post(() => Revalidate(false));
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Post(() =>
    {
        if (quitting || log is null) { return; }
        if (settings.Theme == ThemeChoice.System) { card?.ApplyTheme(settings.Theme, log); }
        if (ThemePolicy.FollowsWindows(settings)) { Render(); }
    });

    private void Render()
    {
        if (quitting || card is null || tray is null) { return; }
        var snapshot = collector?.Latest;
        var presentation = CardFormatter.Format(snapshot is null ? StateBuilder.Build([])
                : StateBuilder.Build(snapshot.Observations, snapshot.Blockages),
            snapshot?.Diagnostics ?? [], clock.GetUtcNow(), snapshot?.WrittenAt);
        card.Render(presentation);
        tray.Update(presentation);
    }

    private WidgetMenuItem[] MenuItems()
    {
        var startup = false;
        try { startup = StartupRegistration.IsEnabled(); }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { log?.Record(WidgetEvent.DesktopFailure, exception: exception); }
        return
        [
            new(settings.Visible ? "Masquer" : "Afficher", ToggleVisible),
            new("Actualiser", () => { if (collector is not null) { _ = collector.RefreshAsync(); } }),
            new("Réglages", OpenSettings),
            new("Lecture Codex en direct", () => _ = ReadDirectAsync()),
            new("Mode d’affichage", Children: Enum.GetValues<DisplayMode>()
                .Select(mode => new WidgetMenuItem(WidgetMenu.ModeLabel(mode), () => ChangeMode(mode), settings.Mode == mode)).ToArray()),
            new("Thème", Children: Enum.GetValues<ThemeChoice>()
                .Select(theme => new WidgetMenuItem(WidgetMenu.ThemeLabel(theme), () => ChangeTheme(theme), settings.Theme == theme)).ToArray()),
            new("Toujours au premier plan", ToggleTopmost, settings.Topmost),
            new("Démarrer avec Windows", ToggleStartup, startup),
            new("Recentrer", () => Revalidate(true)),
            new("Ouvrir le journal", OpenLog),
            new("Quitter", () => _ = QuitAsync()),
        ];
    }

    private void ToggleTopmost()
    {
        settings = settings with { Topmost = !settings.Topmost };
        if (card is not null) { card.Topmost = settings.Topmost; }
        SaveSettings();
        Render();
    }

    private void ChangeMode(DisplayMode mode)
    {
        if (card is null || settings.Mode == mode) { return; }
        SavePosition();
        settings = settings with { Mode = mode };
        card.ApplySettings(settings);
        Render();
        Revalidate(false);
    }

    private void ChangeTheme(ThemeChoice theme)
    {
        settings = settings with { Theme = theme };
        if (log is not null) { card?.ApplyTheme(theme, log); }
        SaveSettings();
        Render();
    }

    private void ToggleVisible() => SetVisible(!settings.Visible);
    private void SetVisible(bool visible)
    {
        if (quitting || card is null) { return; }
        settings = settings with { Visible = visible };
        // The card has no taskbar button: showing it from the tray also brings back a minimized card.
        if (visible) { card.Show(); card.WindowState = WindowState.Normal; Revalidate(false); card.Activate(); }
        else { card.Hide(); }
        SaveSettings();
        Render();
    }

    private void SavePosition()
    {
        if (card is null || !CardWindowState.CanPersistBounds(card.WindowState)) { return; }
        var bounds = NativeDesktop.Bounds(card);
        var size = new CardSize(card.ActualWidth, card.ActualHeight);
        settings = settings with { X = bounds.X, Y = bounds.Y,
            Sizes = DisplaySizing.IsValid(settings.Mode, size) ? (settings.Sizes ?? new ModeSizes()).With(settings.Mode, size) : settings.Sizes };
        SaveSettings();
    }

    private void Revalidate(bool recenter)
    {
        if (quitting || card is null) { return; }
        try { NativeDesktop.Place(card, settings, recenter); SavePosition(); }
        catch (System.ComponentModel.Win32Exception exception) { log?.Record(WidgetEvent.DesktopFailure, exception: exception); }
    }

    private void SaveSettings()
    {
        try { settingsStore?.Save(settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { log?.Record(WidgetEvent.StorageFailure, exception: exception); }
        optionsWindow?.Update(settings);
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled(),
                Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."));
            Render();
        }
        catch (Exception exception) { log?.Record(WidgetEvent.DesktopFailure, exception: exception); }
    }

    private void OpenLog()
    {
        if (log is null) { return; }
        try { using var process = Process.Start(new ProcessStartInfo(log.FilePath) { UseShellExecute = true }); }
        catch (Exception exception) { log.Record(WidgetEvent.DesktopFailure, exception: exception); }
    }

    private async Task QuitAsync()
    {
        if (quitting) { return; }
        quitting = true;
        ageTimer?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SaveSettings();
        if (telemetry is not null) { await telemetry.DisposeAsync(); telemetry = null; }
        if (collector is not null) { collector.Updated -= OnUpdated; await collector.DisposeAsync(); collector = null; }
        if (card is not null) { card.AllowClose = true; card.Close(); }
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        // At log off or shutdown WPF calls Shutdown, which closes every window and still raises Closing.
        // Without this, that close reads as the user hiding the card and the next session starts in the tray.
        if (!e.Cancel && card is not null) { card.AllowClose = true; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        ageTimer?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        DispatcherUnhandledException -= OnDispatcherFailure;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainFailure;
        TaskScheduler.UnobservedTaskException -= OnTaskFailure;
        telemetry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (collector is not null)
        {
            collector.Updated -= OnUpdated;
            collector.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        tray?.Dispose();
        instance?.Dispose();
        log?.Record(WidgetEvent.Stopped);
        GC.SuppressFinalize(this);
    }

    private void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs e)
    { log?.Record(WidgetEvent.DispatcherFailure, exception: e.Exception); e.Handled = true; }
    private void OnDomainFailure(object sender, UnhandledExceptionEventArgs e) =>
        log?.Record(WidgetEvent.DomainFailure, exception: e.ExceptionObject as Exception);
    private void OnTaskFailure(object? sender, UnobservedTaskExceptionEventArgs e)
    { log?.Record(WidgetEvent.TaskFailure, exception: e.Exception); e.SetObserved(); }
}
