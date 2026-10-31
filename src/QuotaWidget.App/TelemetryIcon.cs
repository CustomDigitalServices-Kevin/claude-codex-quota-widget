using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace QuotaWidget.App;

internal sealed class TelemetryIcon : FrameworkElement
{
    private readonly string kind;

    internal TelemetryIcon(string kind)
    {
        this.kind = kind;
        AutomationProperties.SetName(this, kind);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) { return; }
        var brush = (Brush)FindResource("BlueAccent");
        var pen = new Pen(brush, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.PushTransform(new ScaleTransform(ActualWidth / 32, ActualHeight / 32));
        switch (kind)
        {
            case "CPU":
                Box(context, pen, 8, 8, 16, 16);
                Box(context, pen, 12, 12, 8, 8);
                for (var pin = 10; pin <= 22; pin += 4)
                {
                    Line(context, pen, pin, 4, pin, 8); Line(context, pen, pin, 24, pin, 28);
                    Line(context, pen, 4, pin, 8, pin); Line(context, pen, 24, pin, 28, pin);
                }
                break;
            case "RAM":
                Box(context, pen, 3, 7, 26, 16);
                for (var chip = 6; chip <= 22; chip += 8) { Box(context, pen, chip, 10, 4, 8); }
                for (var pin = 6; pin <= 26; pin += 4) { Line(context, pen, pin, 23, pin, 27); }
                break;
            case "GPU":
                Box(context, pen, 2, 7, 25, 17);
                context.DrawEllipse(null, pen, new Point(14, 15.5), 6, 6);
                context.DrawEllipse(brush, null, new Point(14, 15.5), 1.5, 1.5);
                for (var index = 0; index < 5; index++)
                {
                    var angle = index * Math.PI * 2 / 5;
                    Line(context, pen, 14 + Math.Cos(angle) * 2.5, 15.5 + Math.Sin(angle) * 2.5,
                        14 + Math.Cos(angle + 0.5) * 5.3, 15.5 + Math.Sin(angle + 0.5) * 5.3);
                }
                Line(context, pen, 29, 4, 29, 27); Line(context, pen, 5, 25, 10, 25);
                break;
            case "VRAM":
                Box(context, pen, 8, 6, 16, 20);
                for (var pin = 10; pin <= 22; pin += 4)
                { Line(context, pen, 4, pin, 8, pin); Line(context, pen, 24, pin, 28, pin); }
                Box(context, pen, 11, 10, 3.5, 4);
                Box(context, pen, 17.5, 10, 3.5, 4);
                Box(context, pen, 11, 18, 3.5, 4);
                Box(context, pen, 17.5, 18, 3.5, 4);
                break;
            case "Sessions":
                Box(context, pen, 10, 4, 18, 17);
                Box(context, pen, 3, 11, 18, 17);
                Line(context, pen, 6, 16, 18, 16); Line(context, pen, 7, 21, 13, 21);
                break;
        }
        context.Pop();
    }

    private static void Box(DrawingContext context, Pen pen, double x, double y, double width, double height) =>
        context.DrawRoundedRectangle(null, pen, new Rect(x, y, width, height), 2, 2);

    private static void Line(DrawingContext context, Pen pen, double x1, double y1, double x2, double y2) =>
        context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
}
