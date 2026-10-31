using System.Windows;
using System.Windows.Media;

namespace QuotaWidget.App;

// Draw only geometry here. Labels remain native WPF text, at their own font size, in every mode.
internal sealed class QuotaGraph(WindowPresentation window, bool circular) : FrameworkElement
{
    // Two provider sections of gauges fit the default Gauges card without scrolling (capture 2026-10-30).
    internal const double MaxGaugeDiameter = 140;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 160;
        return new Size(width, circular ? Math.Min(width, MaxGaugeDiameter) : 24);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) { return; }
        var track = (Brush)FindResource("BarTrack");
        var fill = (Brush)FindResource(ColorKey(window.Color));
        var marker = new Pen(Fade((Brush)FindResource("PrimaryText"), 0.85), 1.5);
        if (circular)
        {
            var radius = Math.Max(0, Math.Min(width, height) / 2 - 13);
            if (radius <= 0) { return; }
            var center = new Point(width / 2, height / 2);
            DrawTicks(drawingContext, center, radius, track);
            DrawArc(drawingContext, GaugeGeometry.Arc(100), center, radius, track, 8);
            if (window.Percent is > 0)
            {
                var arc = GaugeGeometry.Arc(window.Percent.Value);
                DrawArc(drawingContext, arc, center, radius, Fade(fill, 0.06), 20);
                DrawArc(drawingContext, arc, center, radius, Fade(fill, 0.15), 13);
                DrawArc(drawingContext, arc, center, radius, Highlight(fill), 8);
            }
            if (window.ElapsedFraction is { } elapsed && double.IsFinite(elapsed))
            {
                var point = GaugeGeometry.Point(elapsed);
                drawingContext.DrawLine(marker, At(point, center, radius - 7), At(point, center, radius + 6));
            }
        }
        else
        {
            var barHeight = Math.Min(9, height - 4);
            var y = (height - barHeight) / 2;
            drawingContext.DrawRoundedRectangle(track, null, new Rect(0, y, width, barHeight), barHeight / 2, barHeight / 2);
            var tick = new Pen(Fade((Brush)FindResource("SecondaryText"), 0.15), 0.6);
            for (var i = 1; i < 40; i++)
            {
                var x = width * i / 40;
                drawingContext.DrawLine(tick, new Point(x, y + 1), new Point(x, y + barHeight - 1));
            }
            if (window.Percent is { } percent && double.IsFinite(percent) && percent > 0)
            {
                drawingContext.PushClip(new RectangleGeometry(new Rect(0, y, width, barHeight), barHeight / 2, barHeight / 2));
                drawingContext.DrawRoundedRectangle(Highlight(fill), null, new Rect(0, y, width * Math.Clamp(percent / 100, 0, 1), barHeight), barHeight / 2, barHeight / 2);
                drawingContext.Pop();
            }
            if (window.ElapsedFraction is { } elapsed && double.IsFinite(elapsed))
            {
                var x = Math.Clamp(elapsed * width, 1, Math.Max(1, width - 1));
                drawingContext.DrawLine(marker, new Point(x, y - 2), new Point(x, y + barHeight + 2));
            }
        }
    }

    internal static string ColorKey(string? color) => color switch
    { "vert" => "StateGreen", "orange" => "StateOrange", "rouge" => "StateRed", _ => "SecondaryText" };

    private static Point At(GaugePoint point, Point center, double radius) =>
        new(center.X + point.X * radius, center.Y + point.Y * radius);

    private static void DrawTicks(DrawingContext context, Point center, double radius, Brush track)
    {
        var pen = new Pen(Fade(track, 0.8), 1);
        for (var i = 0; i <= 40; i++)
        {
            var point = GaugeGeometry.Point(i / 40d);
            context.DrawLine(pen, At(point, center, radius + 7), At(point, center, radius + (i % 5 == 0 ? 11 : 9)));
        }
        context.DrawEllipse(null, new Pen(Fade(track, 0.35), 1), center, radius - 9, radius - 9);
    }

    private static Brush Fade(Brush brush, double opacity)
    {
        var result = brush.Clone();
        result.Opacity = opacity;
        result.Freeze();
        return result;
    }

    private static Brush Highlight(Brush brush)
    {
        if (brush is not SolidColorBrush solid) { return brush; }
        var color = solid.Color;
        var light = Color.FromRgb((byte)(color.R + (255 - color.R) * 0.25),
            (byte)(color.G + (255 - color.G) * 0.25), (byte)(color.B + (255 - color.B) * 0.25));
        var gradient = new LinearGradientBrush(light, color, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        return gradient;
    }

    private static void DrawArc(DrawingContext context, GaugeArc arc, Point center, double radius, Brush brush, double thickness)
    {
        if (arc.SweepDegrees <= 0) { return; }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(arc.Start, center, radius), false, false);
            path.ArcTo(At(arc.End, center, radius), new Size(radius, radius), 0, arc.IsLargeArc, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}
