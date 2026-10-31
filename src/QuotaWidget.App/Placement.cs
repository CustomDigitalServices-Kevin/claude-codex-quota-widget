namespace QuotaWidget.App;

public readonly record struct DesktopRect(double X, double Y, double Width, double Height);
public readonly record struct DesktopPoint(double X, double Y);

public static class Placement
{
    // Only sizes are scaled. Monitor origins and saved positions are physical virtual-desktop
    // pixels: multiplying a global DIP origin by one monitor's scale breaks mixed-DPI layouts.
    public static double DipToPixels(double value, double scale) => value * ValidScale(scale);
    public static double PixelsToDip(double value, double scale) => value / ValidScale(scale);

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0
        ? scale : throw new ArgumentOutOfRangeException(nameof(scale));

    public static DesktopPoint Place(DesktopRect card, IReadOnlyList<DesktopRect> workAreas, DesktopRect primary, bool recenter = false)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (!recenter && double.IsFinite(card.X) && double.IsFinite(card.Y) && workAreas.Any(area =>
                Math.Min(card.X + card.Width, area.X + area.Width) - Math.Max(card.X, area.X) >= 120
                && Math.Min(card.Y + card.Height, area.Y + area.Height) - Math.Max(card.Y, area.Y) >= 40))
        {
            return new(card.X, card.Y);
        }
        return new(primary.X + Math.Max(0, (primary.Width - card.Width) / 2),
            primary.Y + Math.Max(0, (primary.Height - card.Height) / 2));
    }
}
