using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuotaWidget.App;

public partial class MainWindow
{
    private Border BuildSection(ProviderPresentation section)
    {
        var design = Preference(section.Provider).DesignChoice ?? mode;
        var compact = design == DisplayMode.Compact;
        var circular = CardVisualPolicy.UsesGauges(design, section.Windows.Length);
        var body = new StackPanel();
        body.Children.Add(SectionHeader(section, compact));
        Panel windows = circular ? new GaugePanel() : new StackPanel();
        foreach (var window in section.Windows)
        {
            windows.Children.Add(compact ? CompactWindow(window) : circular ? GaugeWindow(window) : DetailedWindow(window));
        }
        body.Children.Add(windows);
        var activityHost = new Border { Margin = new Thickness(3, 9, 3, 0), Child = ActivityStrip(section.Provider, lastActivity) };
        activityHosts[section.Provider] = activityHost;
        body.Children.Add(activityHost);
        if (section.StatusText.Length > 0)
        {
            var status = Text(section.StatusText, 12, "SecondaryText");
            status.ToolTip = string.Join('\n', section.Messages);
            status.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(status);
        }
        var surface = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(compact ? 12 : 11),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10), Child = body };
        surface.SetResourceReference(Border.BackgroundProperty, "SectionSurface");
        surface.SetResourceReference(Border.BorderBrushProperty, CardVisualPolicy.ProviderBorder(section.Provider));
        return surface;
    }

    private static Grid SectionHeader(ProviderPresentation section, bool compact)
    {
        var header = new Grid { Margin = new Thickness(2, 0, 2, compact ? 8 : 10), ToolTip = section.HeaderToolTip };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetHelpText(header, section.HeaderToolTip);
        var accent = CardVisualPolicy.ProviderAccent(section.Provider);
        var mark = Text(section.Provider == "claude" ? "AI" : "‹/›", 11, accent, true);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var icon = new Border { Width = 27, Height = 27, CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1), Child = mark, Margin = new Thickness(0, 0, 9, 0) };
        icon.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        icon.SetResourceReference(Border.BorderBrushProperty, accent);
        header.Children.Add(icon);
        var identity = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text(section.Name, compact ? 13 : 15, bold: true));
        if (section.Plan.Length > 0)
        {
            var plan = Pill(section.Plan, "SecondaryText");
            plan.Margin = new Thickness(7, 0, 4, 0);
            identity.Children.Add(plan);
        }
        AddColumn(header, identity, 1);
        AddColumn(header, Pill(section.Badge, QuotaGraph.ColorKey(section.Color), true), 2);
        return header;
    }

    private static Border DetailedWindow(WindowPresentation window)
    {
        var panel = new StackPanel();
        var heading = new DockPanel();
        var percent = Text(window.PercentText, 24, QuotaGraph.ColorKey(window.Color), true);
        percent.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(percent, Dock.Right);
        heading.Children.Add(percent);
        heading.Children.Add(Label(window, false));
        panel.Children.Add(heading);
        panel.Children.Add(Graph(window, false));
        panel.Children.Add(Reset(window));
        return Tile(panel, new Thickness(2, 0, 2, 2));
    }

    private static Grid CompactWindow(WindowPresentation window)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 35 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var label = Label(window, true);
        label.Margin = new Thickness(0, 0, 6, 0);
        grid.Children.Add(label);
        AddColumn(grid, Graph(window, false), 1);
        var percent = Text(window.PercentText, 16, QuotaGraph.ColorKey(window.Color), true);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        AddColumn(grid, percent, 2);
        AddColumn(grid, Text(window.ResetText, 11, "SecondaryText"), 3);
        return grid;
    }

    private static Border GaugeWindow(WindowPresentation window)
    {
        var panel = new StackPanel();
        var label = Label(window, false);
        label.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(label);
        var dial = new Grid();
        dial.Children.Add(Graph(window, true));
        var reading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var percent = Text(window.PercentText, 28, QuotaGraph.ColorKey(window.Color), true);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        reading.Children.Add(percent);
        var caption = Text(window.Percent.HasValue ? "utilisés" : "inconnu", 10, "SecondaryText");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        reading.Children.Add(caption);
        dial.Children.Add(reading);
        panel.Children.Add(dial);
        var reset = Reset(window);
        reset.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(reset);
        return Tile(panel, new Thickness(3, 0, 3, 2));
    }

    private static Border Tile(UIElement child, Thickness margin)
    {
        var tile = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 8, 9, 9),
            Margin = margin, BorderThickness = new Thickness(1), Child = child };
        tile.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, "SurfaceBorder");
        return tile;
    }

    private static QuotaGraph Graph(WindowPresentation window, bool circular)
    {
        var graph = new QuotaGraph(window, circular) { VerticalAlignment = VerticalAlignment.Center,
            ToolTip = window.PaceToolTip.Length > 0 ? window.PaceToolTip : null };
        if (!circular) { graph.Height = 24; }
        AutomationProperties.SetName(graph, window.AccessibleText);
        return graph;
    }

    private static DockPanel Label(WindowPresentation window, bool shortLabel)
    {
        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (window.IsStale)
        {
            var stale = Symbol("\uE7BA", "StateOrange");
            stale.Margin = new Thickness(5, 0, 0, 0);
            stale.ToolTip = window.AgeText;
            AutomationProperties.SetName(stale, "Mesure ancienne : " + window.AgeText);
            DockPanel.SetDock(stale, Dock.Right);
            panel.Children.Add(stale);
        }
        if (!shortLabel)
        {
            var icon = Symbol(window.Type == "weekly" ? "\uE787" : "\uE823", "SecondaryText");
            icon.Margin = new Thickness(0, 0, 5, 0);
            panel.Children.Add(icon);
        }
        panel.Children.Add(Text(shortLabel ? window.ShortLabel : window.Label, shortLabel ? 12 : 11));
        return panel;
    }

    private static DockPanel Reset(WindowPresentation window)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var icon = Symbol("\uE787", "SecondaryText");
        icon.FontSize = 9;
        icon.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(icon);
        panel.Children.Add(Text(window.ResetText, 10, "SecondaryText"));
        return panel;
    }

    private static TextBlock Symbol(string glyph, string color)
    {
        var symbol = Text(glyph, 11, color);
        symbol.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        return symbol;
    }

    private static TextBlock Text(string value, double size, string color = "PrimaryText", bool bold = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, color);
        return text;
    }

    private static Border Pill(string value, string color, bool status = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (status)
        {
            var dot = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, color);
            content.Children.Add(dot);
        }
        content.Children.Add(Text(value, 10, color));
        var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0.7), Child = content };
        pill.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        pill.SetResourceReference(Border.BorderBrushProperty, color);
        return pill;
    }

    private static void AddColumn(Grid grid, UIElement child, int column)
    { Grid.SetColumn(child, column); grid.Children.Add(child); }
}
