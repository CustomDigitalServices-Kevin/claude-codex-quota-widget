using System.Windows;
using System.Windows.Media;

namespace QuotaWidget.App;

internal sealed class PcDial : FrameworkElement
{
    private double? percent;

    internal PcDial(double? percent) => this.percent = percent;

    internal double? Percent
    {
        get => percent;
        set
        {
            if (Nullable.Equals(percent, value)) { return; }
            percent = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) { return; }

        const double stroke = 5;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, size / 2 - stroke / 2 - 1);
        var track = new Pen((Brush)FindResource("BarTrack"), stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        drawingContext.DrawEllipse(null, track, center, radius, radius);

        if (percent is not { } value || !double.IsFinite(value) || value <= 0) { return; }
        var fraction = Math.Clamp(value / 100, 0, 1);
        var accent = new Pen((Brush)FindResource("BlueAccent"), stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        if (fraction >= 0.9999)
        {
            drawingContext.DrawEllipse(null, accent, center, radius, radius);
            return;
        }

        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + fraction * 360);
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5,
                SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        drawingContext.DrawGeometry(null, accent, arc);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }
}
