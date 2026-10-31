using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QuotaWidget.App;

public partial class MainWindow
{
    private readonly Dictionary<string, Border> activityHosts = new(StringComparer.Ordinal);
    private RecentActivitySnapshot? lastActivity;
    private bool fittingContent;

    private SystemTelemetrySnapshot? lastSystem;
    private CardPresentation? lastPresentation;
    private CardPreference[] cardPreferences = CardPreferences.Normalize(null);
    private readonly Dictionary<string, Border> cardSurfaces = new(StringComparer.Ordinal);
    private bool forceFitRequested;

    public void RenderTelemetry(SystemTelemetrySnapshot? snapshot, RecentActivitySnapshot? activity = null)
    {
        lastSystem = snapshot;
        var activityChanged = (lastActivity?.Sessions, lastActivity?.ClaudeSessions, lastActivity?.IsPartial, lastActivity?.Detail,
                HasReferences(lastActivity, "claude"), HasReferences(lastActivity, "codex"))
            != (activity?.Sessions, activity?.ClaudeSessions, activity?.IsPartial, activity?.Detail,
                HasReferences(activity, "claude"), HasReferences(activity, "codex"));
        lastActivity = activity;
        if (activityChanged)
        {
            foreach (var (provider, host) in activityHosts) { host.Child = ActivityStrip(provider, activity); }
        }
        SynchronizeSessionReferences();
        if (cardSurfaces.TryGetValue("pc", out var existing))
        {
            var preference = Preference("pc");
            var design = preference.DesignChoice ?? mode;
            if (UpdatePcCard(existing, snapshot, design)) { FitToContent(); return; }
            var replacement = BuildPcCard(snapshot, design);
            AutomationProperties.SetAutomationId(replacement, "pc");
            ApplyCardTheme(replacement, preference);
            var index = Sections.Children.IndexOf(existing);
            Sections.Children.RemoveAt(index);
            Sections.Children.Insert(index, replacement);
            cardSurfaces["pc"] = replacement;
        }
        FitToContent();
    }

    private static bool HasReferences(RecentActivitySnapshot? activity, string provider) =>
        activity?.References?.Any(reference => reference.Provider == provider) == true;

    private void ApplyCardPreferences(CardPreference[]? preferences)
    {
        cardPreferences = CardPreferences.Normalize(preferences);
        forceFitRequested = true;
    }

    private CardPreference Preference(string id) => cardPreferences.First(card => card.Id == id);

    private void RenderCards()
    {
        activityHosts.Clear();
        sessionToggles.Clear();
        cardSurfaces.Clear();
        Sections.Children.Clear();
        foreach (var preference in cardPreferences.Where(card => card.Visible))
        {
            Border? surface = null;
            if (preference.Id == "pc") { surface = BuildPcCard(lastSystem, preference.DesignChoice ?? mode); }
            else if (lastPresentation?.Sections.FirstOrDefault(section => section.Provider == preference.Id) is { } section)
            { surface = BuildSection(section); }
            if (surface is null) { continue; }
            ApplyCardTheme(surface, preference);
            AutomationProperties.SetAutomationId(surface, preference.Id);
            cardSurfaces[preference.Id] = surface;
            Sections.Children.Add(surface);
        }
        CloseUnavailableSessionDetails();
        FitToContent();
    }

    private void ApplyCardTheme(Border surface, CardPreference preference)
    {
        if (preference.ThemeChoice is not { } choice) { return; }
        var effective = choice == ThemeChoice.System && log is not null ? ThemeResources.Resolve(choice, log) : choice;
        surface.Resources.MergedDictionaries.Add(ThemeResources.For(effective));
    }
    private StackPanel ActivityStrip(string provider, RecentActivitySnapshot? activity)
    {
        var area = new StackPanel();
        var body = new DockPanel();
        var sessions = provider == "claude" ? activity?.ClaudeSessions : activity?.Sessions;
        if (sessions is not > 0) { area.Visibility = Visibility.Collapsed; return area; }
        var period = Text("15 min", 10, "SecondaryText");
        period.ToolTip = "Sessions observées sur les quinze dernières minutes.";
        DockPanel.SetDock(period, Dock.Right);
        body.Children.Add(period);
        var counts = new StackPanel { Orientation = Orientation.Horizontal };
        counts.Children.Add(SessionCount(provider, sessions.Value, activity));
        if (activity?.References?.Any(reference => reference.Provider == provider) == true)
        { counts.Children.Add(SessionDetailsButton(provider)); }
        body.Children.Add(counts);
        area.Children.Add(body);
        if (SessionState(provider).Expanded) { area.Children.Add(SessionDetailsPanel(provider)); }
        return area;
    }

    private static Border SessionCount(string provider, int count, RecentActivitySnapshot? activity)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TelemetryIcon("Sessions") { Width = 19, Height = 19, Margin = new Thickness(0, 0, 6, 0) });
        var reading = count.ToString(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        panel.Children.Add(Text(reading + (count == 1 ? " session" : " sessions"), 12, CardVisualPolicy.ProviderAccent(provider), true));
        var chip = new Border { Child = panel, Margin = new Thickness(0, 0, 9, 0), Padding = new Thickness(8, 4, 9, 4), CornerRadius = new CornerRadius(8) };
        chip.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        chip.ToolTip = (provider == "claude" ? "Claude : " : "Codex : ") + "sessions observées sur 15 min : " + reading
            + "\nCes observations ne prouvent pas une activité en cours.\n" + activity?.Detail;
        AutomationProperties.SetName(chip, (string)chip.ToolTip);
        return chip;
    }

    private static Border MetricTile(MachineMetric metric)
    {
        var panel = new StackPanel { ToolTip = metric.ToolTip };
        panel.Children.Add(new TelemetryIcon(metric.Label) { Width = 29, Height = 29,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
        var value = Text(metric.Value, metric.Value == "Indisponible" ? 12 : 21, "BlueAccent", true);
        value.Margin = new Thickness(0, 3, 0, 0);
        value.Tag = "pc-value";
        value.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(value);
        var detail = Text(metric.Label, 9, "SecondaryText");
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        detail.TextWrapping = TextWrapping.NoWrap;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(detail);
        panel.Children.Add(new MachineMeter(metric.Percent) { Height = 9, Margin = new Thickness(0, 5, 0, 0) });
        var tile = Tile(panel, new Thickness(3));
        AutomationProperties.SetAutomationId(tile, metric.Label);
        AutomationProperties.SetName(tile, $"{metric.Label} : {metric.Value}, {metric.Detail}");
        return tile;
    }

    private void FitToContent()
    {
        if (!IsLoaded || applyingSize || fittingContent) { return; }
        fittingContent = true;
        try
        {
            var maximum = NativeDesktop.AvailableHeight(this);
            UpdateLayout();
            var required = Math.Ceiling(ActualHeight - CardScroll.ViewportHeight + CardScroll.ExtentHeight) + 2;
            MinHeight = Math.Min(maximum, Math.Max(85, required));
            MaxHeight = maximum;
            Height = Math.Clamp(forceFitRequested ? required : Height, MinHeight, maximum);
            forceFitRequested = false;
            UpdateLayout();
            NativeDesktop.KeepFullyVisible(this);
        }
        finally { fittingContent = false; }
    }
}

internal sealed class MachineMeter : FrameworkElement
{
    private double? percent;
    internal MachineMeter(double? percent) => this.percent = percent;
    internal double? Percent { get => percent; set { percent = value; InvalidateVisual(); } }
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0) { return; }
        drawingContext.DrawRoundedRectangle((Brush)FindResource("BarTrack"), null, new Rect(0, 2, ActualWidth, 4), 2, 2);
        if (percent is { } value && double.IsFinite(value) && value > 0)
        {
            drawingContext.DrawRoundedRectangle((Brush)FindResource("BlueAccent"), null,
                new Rect(0, 2, ActualWidth * Math.Clamp(value / 100, 0, 1), 4), 2, 2);
        }
    }
}
