using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QuotaWidget.App;

public partial class MainWindow
{
    private Border SessionDetailsPanel(string provider)
    {
        var state = SessionState(provider);
        var accent = CardVisualPolicy.ProviderAccent(provider);
        var content = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(1, 0, 0, 9) };
        var refresh = new Button { Content = Symbol("\uE72C", accent), Style = (Style)FindResource("IconButton"),
            Width = 24, Height = 24, IsEnabled = !state.Loading, ToolTip = "Actualiser les détails" };
        DockPanel.SetDock(refresh, Dock.Right);
        AutomationProperties.SetName(refresh, "Actualiser les détails " + (provider == "claude" ? "Claude" : "Codex"));
        AutomationProperties.SetAutomationId(refresh, provider + "-sessions-refresh");
        refresh.Click += async (_, _) => await ReloadSessionDetailsAsync(provider);
        heading.Children.Add(refresh);
        var note = Text(state.Loading ? "Actualisation…" : "Dernier contexte enregistré", 10, "SecondaryText");
        heading.Children.Add(note);
        content.Children.Add(heading);
        if (state.Loading && state.Details.Length == 0) { content.Children.Add(Text("Chargement…", 12, accent)); }
        else if (state.Failed) { content.Children.Add(Text("Lecture indisponible. Utilise Actualiser pour réessayer.", 12, "SecondaryText")); }
        else if (state.Details.Length == 0) { content.Children.Add(Text("Aucun détail disponible pour ces sessions.", 12, "SecondaryText")); }
        else
        {
            var rows = state.Details.Select(detail => SessionRowPresentation.From(detail, DateTimeOffset.Now)).ToArray();
            foreach (var row in rows.Where(row => row.HasDetails)) { content.Children.Add(SessionRow(row, accent)); }
            var missing = rows.Count(row => !row.HasDetails);
            if (missing > 0)
            {
                var count = missing.ToString(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
                content.Children.Add(Text(count + (missing == 1 ? " session sans détails disponibles." : " sessions sans détails disponibles."),
                    10, "SecondaryText"));
            }
        }
        if (state.References.Length > 32) { content.Children.Add(Text("Les 32 sessions les plus récentes sont affichées.", 10, "SecondaryText")); }
        var panel = new Border { Child = content, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 9, 0, 0) };
        panel.SetResourceReference(Border.BorderBrushProperty, CardVisualPolicy.ProviderBorder(provider));
        AutomationProperties.SetAutomationId(panel, provider + "-sessions-expanded");
        return panel;
    }

    private static Border SessionRow(SessionRowPresentation row, string accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Border { Width = 23, Height = 23, CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1), Child = Symbol("\uE716", accent), VerticalAlignment = VerticalAlignment.Top };
        avatar.SetResourceReference(Border.BorderBrushProperty, accent);
        var glyph = (TextBlock)avatar.Child;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(avatar);
        var body = new StackPanel { Margin = new Thickness(5, 0, 0, 0) };
        var title = Text(row.Title, 13, bold: true);
        title.ToolTip = "Sujet enregistré";
        body.Children.Add(title);
        if (row.Agent is { } agent)
        {
            var agentText = Text("Agent : " + agent, 11, accent);
            agentText.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(agentText);
        }
        var labels = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (row.Model is { } model)
        {
            var pill = Pill(model, accent);
            pill.Margin = new Thickness(0, 0, 6, 4);
            labels.Children.Add(pill);
        }
        if (row.Effort is { } effort)
        {
            var effortText = Text(effort, 10, "SecondaryText");
            effortText.Margin = new Thickness(0, 0, 0, 4);
            labels.Children.Add(effortText);
        }
        if (labels.Children.Count > 0) { body.Children.Add(labels); }
        body.Children.Add(SessionTime(row.Time));
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return Tile(grid, new Thickness(0, 0, 0, 7));
    }

    private static StackPanel SessionTime(string value)
    {
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var clock = Symbol("\uE823", "SecondaryText");
        clock.FontSize = 10;
        clock.Margin = new Thickness(0, 0, 5, 0);
        time.Children.Add(clock);
        time.Children.Add(Text(value, 10, "SecondaryText"));
        return time;
    }
}
