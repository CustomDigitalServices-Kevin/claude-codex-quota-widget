using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuotaWidget.App.Tests;

[Collection("WPF rendering")]
public sealed class WidgetRenderingTests
{
    private static readonly string[] ThemeKeys = ["CardChrome", "FrameBorder", "TileSurface", "SurfaceBorder",
        "ClaudeAccent", "CodexAccent", "ClaudeBorder", "CodexBorder", "BlueAccent"];
    private static readonly string[] MonitoringLabels = ["CPU", "RAM", "GPU", "VRAM"];
    [Theory]
    [InlineData(DisplayMode.Detailed, ThemeChoice.Dark)]
    [InlineData(DisplayMode.Detailed, ThemeChoice.Light)]
    [InlineData(DisplayMode.Compact, ThemeChoice.Dark)]
    [InlineData(DisplayMode.Compact, ThemeChoice.Light)]
    [InlineData(DisplayMode.Gauges, ThemeChoice.Dark)]
    [InlineData(DisplayMode.Gauges, ThemeChoice.Light)]
    [InlineData(DisplayMode.Detailed, ThemeChoice.Black)]
    [InlineData(DisplayMode.Compact, ThemeChoice.Black)]
    [InlineData(DisplayMode.Gauges, ThemeChoice.Black)]
    public void EveryModeRendersRealWpfControlsAtMinimumAndDefaultSize(DisplayMode mode, ThemeChoice theme) => RunSta(() =>
    {
        var window = new MainWindow { AllowClose = true };
        try
        {
            var state = TestData.State with { Codex = TestData.State.Codex with { Plan = "pro" } };
            var presentation = CardFormatter.Format(state, [], TestData.Now, TestData.Now);
            window.ApplyTestTheme(theme);
            window.Render(presentation);
            foreach (var size in new[] { DisplaySizing.Minimum(mode), DisplaySizing.Default(mode) })
            {
                window.ApplyTestMode(mode, size);
                window.Render(presentation);
                window.RenderTelemetry(new(TestData.Now, 25, 12UL << 30, 32UL << 30,
                    [new("test-adapter", "Test GPU with an intentionally long adapter name", 12, 2UL << 30, 16UL << 30, 1UL << 30)]),
                    new(TestData.Now, 2, true, "Synthetic session observation fixture."));
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.Measure(new Size(size.Width, size.Height));
                root.Arrange(new Rect(0, 0, size.Width, size.Height));
                root.UpdateLayout();
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(root);
                var textControls = Descendants(root).OfType<TextBlock>().ToArray();
                var texts = textControls.Select(text => text.Text).ToArray();
                Assert.DoesNotContain("ND", texts);
                Assert.DoesNotContain(Descendants(root), element => AutomationProperties.GetName(element) == "Agents");
                foreach (var item in presentation.Sections.SelectMany(section => section.Windows))
                {
                    Assert.Contains(item.PercentText, texts);
                    Assert.Contains(item.ResetText, texts);
                    Assert.All(textControls.Where(text => text.Text == item.PercentText || text.Text == item.ResetText),
                        text => Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0));
                }
                Assert.Contains("Claude", texts);
                Assert.Contains("Codex", texts);
                Assert.Contains(presentation.Header.AgeText, texts);
                foreach (var label in MonitoringLabels)
                { Assert.Contains(label, texts); }
                Assert.Contains(presentation.Sections[0].Windows[0].AccessibleText,
                    Descendants(root).Select(AutomationProperties.GetName));
                foreach (var key in ThemeKeys)
                { Assert.IsAssignableFrom<Brush>(window.FindResource(key)); }
                var graphs = Descendants(root).OfType<FrameworkElement>().Where(item => item.GetType().Name == "QuotaGraph").ToArray();
                Assert.Equal(3, graphs.Length);
                Assert.Equal(mode == DisplayMode.Compact ? 0 : mode == DisplayMode.Gauges ? 3 : 2,
                    graphs.Count(graph => graph.ActualHeight > 24));
                foreach (var graph in graphs)
                { Assert.True(graph.ActualWidth > 0 && graph.ActualHeight > 0); }
                foreach (var scroll in Descendants(root).OfType<ScrollViewer>())
                { Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "The widget must not overflow horizontally."); }
            }
        }
        finally { window.Close(); }
    });

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF rendering exceeded 20 seconds.");
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }
}

internal static class WindowTestExtensions
{
    // Exercise the same internal entry points the application uses without changing its public API.
    internal static void ApplyTestMode(this MainWindow window, DisplayMode mode, CardSize size) =>
        typeof(MainWindow).GetMethod("ApplySettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [new WidgetSettings(Mode: mode, Sizes: new ModeSizes().With(mode, size))]);

    internal static void ApplyTestTheme(this MainWindow window, ThemeChoice theme)
    {
        var palette = theme == ThemeChoice.Dark ? ThemePalette.Dark : theme == ThemeChoice.Black ? ThemePalette.Black : ThemePalette.Light;
        window.Resources.MergedDictionaries.Clear();
        window.Resources.MergedDictionaries.Add(ThemeResources.For(theme));
        Assert.Equal(palette.Text, ToRgb(((SolidColorBrush)window.FindResource("PrimaryText")).Color));
    }

    private static uint ToRgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
}
