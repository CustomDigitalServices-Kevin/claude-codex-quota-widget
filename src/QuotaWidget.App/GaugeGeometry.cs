namespace QuotaWidget.App;

public readonly record struct GaugePoint(double X, double Y);
public readonly record struct GaugeArc(GaugePoint Start, GaugePoint End, double SweepDegrees, bool IsLargeArc);

public static class GaugeGeometry
{
    // Unit circle, clockwise from bottom left; the bottom 90 degrees stay open.
    public static GaugePoint Point(double fraction)
    {
        var angle = (135 + 270 * Clamp(fraction)) * Math.PI / 180;
        return new(Math.Cos(angle), Math.Sin(angle));
    }

    public static GaugeArc Arc(double percent)
    {
        var fraction = Clamp(percent / 100);
        var sweep = 270 * fraction;
        return new(Point(0), Point(fraction), sweep, sweep > 180);
    }

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
