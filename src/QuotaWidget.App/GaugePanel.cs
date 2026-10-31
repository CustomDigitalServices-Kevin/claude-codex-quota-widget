using System.Windows;
using System.Windows.Controls;

namespace QuotaWidget.App;

// Reflow gauges at a readable width, then share all available horizontal space. Text never scales.
internal sealed class GaugePanel : Panel
{
    private int columns = 1;
    private double rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 320;
        columns = Math.Max(1, Math.Min(InternalChildren.Count, (int)(width / 148)));
        var cellWidth = width / columns;
        rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return new Size(width, Math.Ceiling((double)InternalChildren.Count / columns) * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width / columns;
        for (var i = 0; i < InternalChildren.Count; i++)
        { InternalChildren[i].Arrange(new Rect(i % columns * width, i / columns * rowHeight, width, rowHeight)); }
        return finalSize;
    }
}
