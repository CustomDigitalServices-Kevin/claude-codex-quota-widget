using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace QuotaWidget.App;

public partial class MainWindow
{
    private sealed record PcMetricCache(
        string Label,
        Border Tile,
        FrameworkElement ToolTipHost,
        TextBlock Value,
        MachineMeter? Meter,
        PcDial? Dial);

    private sealed record PcCardCache(
        DisplayMode Design,
        StackPanel Body,
        TextBlock Time,
        PcMetricCache[] Metrics,
        StackPanel GpuDetails);

    internal Border BuildPcCard(SystemTelemetrySnapshot? snapshot, DisplayMode design)
    {
        var body = new StackPanel();
        body.Children.Add(PcHeader(snapshot, out var time));

        var metrics = TelemetryPresentation.Format(snapshot);
        var controls = new List<PcMetricCache>(metrics.Length);
        switch (design)
        {
            case DisplayMode.Compact:
                var rows = new StackPanel();
                foreach (var metric in metrics)
                {
                    var tile = CompactPcMetric(metric, out var cache);
                    rows.Children.Add(tile);
                    controls.Add(cache);
                }
                body.Children.Add(rows);
                break;

            case DisplayMode.Gauges:
                var gauges = new UniformGrid { Columns = Width >= 440 ? 4 : 2 };
                foreach (var metric in metrics)
                {
                    var tile = GaugePcMetric(metric, out var cache);
                    gauges.Children.Add(tile);
                    controls.Add(cache);
                }
                body.Children.Add(gauges);
                break;

            default:
                var tiles = new UniformGrid { Columns = Width >= 440 ? 4 : 2 };
                foreach (var metric in metrics)
                {
                    var tile = MetricTile(metric);
                    tiles.Children.Add(tile);
                    controls.Add(CaptureDetailedMetric(metric, tile));
                }
                body.Children.Add(tiles);
                break;
        }

        var gpuDetails = new StackPanel();
        UpdateGpuDetails(gpuDetails, snapshot);
        body.Children.Add(gpuDetails);
        var surface = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(design == DisplayMode.Compact ? 12 : 11),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 10),
            Child = body,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SectionSurface");
        surface.SetResourceReference(Border.BorderBrushProperty, "SurfaceBorder");
        AutomationProperties.SetName(surface, "Ce PC, mesures processeur, mémoire et cartes graphiques");
        surface.Tag = new PcCardCache(design, body, time, [.. controls], gpuDetails);
        return surface;
    }

    internal static bool UpdatePcCard(Border surface, SystemTelemetrySnapshot? snapshot, DisplayMode design)
    {
        if (surface.Tag is not PcCardCache cache || cache.Design != design ||
            !ReferenceEquals(surface.Child, cache.Body))
        {
            return false;
        }

        var metrics = TelemetryPresentation.Format(snapshot);
        if (metrics.Length != cache.Metrics.Length) { return false; }
        for (var index = 0; index < metrics.Length; index++)
        {
            var metric = metrics[index];
            var controls = cache.Metrics[index];
            if (!string.Equals(metric.Label, controls.Label, StringComparison.Ordinal)) { return false; }
            controls.Value.Text = metric.Value;
            controls.Value.FontSize = ValueSize(design, metric.Value);
            controls.ToolTipHost.ToolTip = metric.ToolTip;
            if (controls.Meter is not null) { controls.Meter.Percent = metric.Percent; }
            if (controls.Dial is not null) { controls.Dial.Percent = metric.Percent; }
            AutomationProperties.SetName(controls.Tile, $"{metric.Label} : {metric.Value}, {metric.Detail}");
        }

        cache.Time.Text = PcTime(snapshot);
        UpdateGpuDetails(cache.GpuDetails, snapshot);
        return true;
    }

    private static DockPanel PcHeader(SystemTelemetrySnapshot? snapshot, out TextBlock time)
    {
        var heading = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        time = Text(PcTime(snapshot), 10, "SecondaryText");
        time.ToolTip = "Mesures système actualisées toutes les 4 secondes.";
        DockPanel.SetDock(time, Dock.Right);
        heading.Children.Add(time);
        heading.Children.Add(Text("Ce PC", 14, bold: true));
        return heading;
    }

    private static string PcTime(SystemTelemetrySnapshot? snapshot) => snapshot is null
        ? "relevé en cours"
        : snapshot.SampledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR"));

    private static Border CompactPcMetric(MachineMetric metric, out PcMetricCache cache)
    {
        var row = new Grid { ToolTip = metric.ToolTip, MinHeight = 39 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 36 });

        var icon = new TelemetryIcon(metric.Label)
        {
            Width = 27,
            Height = 27,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);

        var label = Text(metric.Label, 11, "SecondaryText", true);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        AddColumn(row, label, 1);

        var value = Text(metric.Value, metric.Value == "Indisponible" ? 10 : 13, "BlueAccent", true);
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.Margin = new Thickness(0, 0, 7, 0);
        value.Tag = "pc-value";
        AddColumn(row, value, 2);

        var meter = new MachineMeter(metric.Percent)
        {
            Height = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AddColumn(row, meter, 3);

        var result = new Border
        {
            Child = row,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        result.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        result.SetResourceReference(Border.BorderBrushProperty, "SurfaceBorder");
        AutomationProperties.SetName(result, $"{metric.Label} : {metric.Value}, {metric.Detail}");
        AutomationProperties.SetAutomationId(result, metric.Label);
        cache = new PcMetricCache(metric.Label, result, row, value, meter, null);
        return result;
    }

    private static Border GaugePcMetric(MachineMetric metric, out PcMetricCache cache)
    {
        var content = new StackPanel { ToolTip = metric.ToolTip };
        var dial = new Grid
        {
            Width = 76,
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var gauge = new PcDial(metric.Percent);
        dial.Children.Add(gauge);

        var reading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
        };
        reading.Children.Add(new TelemetryIcon(metric.Label)
        {
            Width = 25,
            Height = 25,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1),
        });
        var value = Text(metric.Value, metric.Value == "Indisponible" ? 8 : 13, "BlueAccent", true);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.TextWrapping = TextWrapping.NoWrap;
        value.Tag = "pc-value";
        reading.Children.Add(value);
        dial.Children.Add(reading);
        content.Children.Add(dial);

        var label = Text(metric.Label, 9, "SecondaryText", true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 2, 0, 0);
        content.Children.Add(label);

        var tile = Tile(content, new Thickness(3));
        AutomationProperties.SetName(tile, $"{metric.Label} : {metric.Value}, {metric.Detail}");
        AutomationProperties.SetAutomationId(tile, metric.Label);
        cache = new PcMetricCache(metric.Label, tile, content, value, null, gauge);
        return tile;
    }

    private static PcMetricCache CaptureDetailedMetric(MachineMetric metric, Border tile)
    {
        if (tile.Child is not StackPanel panel) { throw new InvalidOperationException("Invalid PC metric tile."); }
        var value = panel.Children.OfType<TextBlock>().Single(item => Equals(item.Tag, "pc-value"));
        var meter = panel.Children.OfType<MachineMeter>().Single();
        return new PcMetricCache(metric.Label, tile, panel, value, meter, null);
    }

    private static void UpdateGpuDetails(StackPanel panel, SystemTelemetrySnapshot? snapshot)
    {
        var gpus = snapshot?.Gpus ?? [];
        if (panel.Children.Count != gpus.Length)
        {
            panel.Children.Clear();
            foreach (var unused in gpus) { panel.Children.Add(GpuDetail()); }
        }

        for (var index = 0; index < gpus.Length; index++)
        {
            var gpu = gpus[index];
            var detail = (TextBlock)panel.Children[index];
            detail.Text = gpu.Name + " · " + TelemetryPresentation.Percent(gpu.UtilizationPercent)
                + " · VRAM " + TelemetryPresentation.Bytes(gpu.DedicatedUsedBytes) + " / "
                + TelemetryPresentation.Bytes(gpu.DedicatedTotalBytes);
            detail.ToolTip = "Mémoire partagée : " + TelemetryPresentation.Bytes(gpu.SharedUsedBytes);
            AutomationProperties.SetName(detail, detail.Text + ", " + detail.ToolTip);
        }
    }

    private static TextBlock GpuDetail()
    {
        var detail = Text(string.Empty, 10, "SecondaryText");
        detail.Margin = new Thickness(3, 5, 3, 0);
        detail.TextWrapping = TextWrapping.Wrap;
        return detail;
    }

    private static double ValueSize(DisplayMode design, string value) => design switch
    {
        DisplayMode.Compact => value == "Indisponible" ? 10 : 13,
        DisplayMode.Gauges => value == "Indisponible" ? 8 : 13,
        _ => value == "Indisponible" ? 12 : 21,
    };
}
