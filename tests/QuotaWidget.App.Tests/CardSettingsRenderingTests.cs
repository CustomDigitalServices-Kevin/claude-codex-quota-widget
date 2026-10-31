using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QuotaWidget.App.Tests;

[Collection("WPF rendering")]
public sealed class CardSettingsRenderingTests
{
    private static readonly string[] MovedOrder = ["claude", "pc", "codex"];

    [Theory]
    [InlineData(DisplayMode.Detailed)]
    [InlineData(DisplayMode.Compact)]
    [InlineData(DisplayMode.Gauges)]
    public void PcTelemetryUpdatesExistingControlsInEveryDesign(DisplayMode design) => WidgetRenderingTests.RunSta(() =>
    {
        var window = new MainWindow { AllowClose = true };
        try
        {
            window.ApplySettings(new WidgetSettings(Cards: [new("pc", Design: design.ToString()), new("claude", false), new("codex", false)]));
            window.Render(CardFormatter.Format(TestData.State, [], TestData.Now, TestData.Now));
            window.RenderTelemetry(new(TestData.Now, 10, 4UL << 30, 16UL << 30, []));
            var panel = (StackPanel)window.FindName("Sections");
            var original = Assert.Single(panel.Children.Cast<Border>());
            window.RenderTelemetry(new(TestData.Now.AddSeconds(4), 75, 8UL << 30, 16UL << 30, []));
            Assert.Same(original, Assert.Single(panel.Children.Cast<Border>()));
            Assert.Contains(LogicalDescendants(original).OfType<TextBlock>(), text => text.Text == "75 %");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LoadedWindowShrinksAndGrowsWhenCardsAreRemovedOrRestored() => WidgetRenderingTests.RunSta(() =>
    {
        var window = new MainWindow { AllowClose = true, Opacity = 0 };
        var presentation = CardFormatter.Format(TestData.State, [], TestData.Now, TestData.Now);
        try
        {
            window.ApplySettings(new WidgetSettings());
            window.Render(presentation);
            window.Show();
            window.UpdateLayout();
            var full = window.ActualHeight;
            window.ApplySettings(new WidgetSettings(Cards: [new("pc", false), new("claude", false), new("codex", false)]));
            window.Render(presentation);
            window.UpdateLayout();
            var empty = window.ActualHeight;
            Assert.True(empty < full && empty < 130);
            window.ApplySettings(new WidgetSettings(Cards: [new("pc"), new("claude", false), new("codex", false)]));
            window.Render(presentation);
            window.UpdateLayout();
            Assert.True(window.ActualHeight > empty && window.ActualHeight < full);
            var scroll = (ScrollViewer)window.FindName("CardScroll");
            Assert.True(scroll.ExtentHeight <= scroll.ViewportHeight + 1);
        }
        finally { window.Close(); }
    });
    [Fact]
    public void EveryVisibilityCombinationPreservesOrderAndKeepsSettingsAccessible() => WidgetRenderingTests.RunSta(() =>
    {
        var window = new MainWindow { AllowClose = true };
        try
        {
            var presentation = CardFormatter.Format(TestData.State, [], TestData.Now, TestData.Now);
            for (var mask = 0; mask < 8; mask++)
            {
                var cards = new[] { new CardPreference("pc", (mask & 1) != 0, "Gauges", "Black"),
                    new CardPreference("codex", (mask & 2) != 0, "Compact", "Light"),
                    new CardPreference("claude", (mask & 4) != 0, "Detailed", "Dark") };
                window.ApplySettings(new WidgetSettings(Cards: cards));
                window.Render(presentation);
                window.RenderTelemetry(null);
                var panel = Assert.IsType<StackPanel>(window.FindName("Sections"));
                Assert.Equal(cards.Where(card => card.Visible).Select(card => card.Id),
                    panel.Children.Cast<Border>().Select(AutomationProperties.GetAutomationId));
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.Measure(new Size(460, 1000));
                root.Arrange(new Rect(0, 0, 460, 1000));
                root.UpdateLayout();
                Assert.Contains(WidgetRenderingTests.Descendants(root).OfType<Button>(),
                    button => AutomationProperties.GetName(button) == "Réglages" && button.ActualWidth > 0);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SettingsControlsApplyVisibilityAndOrderImmediately() => WidgetRenderingTests.RunSta(() =>
    {
        var owner = new MainWindow { AllowClose = true, Opacity = 0 };
        owner.Show();
        WidgetSettings current = new();
        SettingsWindow? dialog = null;
        try
        {
            dialog = new SettingsWindow(owner, current, updated => { current = updated; dialog?.Update(updated); }) { Opacity = 0 };
            var cards = Assert.IsType<StackPanel>(dialog.FindName("CardsPanel"));
            var claude = LogicalDescendants(cards).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Afficher Claude");
            claude.IsChecked = false;
            Assert.False(CardPreferences.Normalize(current.Cards).First(card => card.Id == "claude").Visible);
            var move = LogicalDescendants(cards).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Monter Ce PC");
            move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MovedOrder, CardPreferences.Normalize(current.Cards).Select(card => card.Id));
            dialog.MaxWidth = 300;
            dialog.MaxHeight = 300;
            dialog.Update(current);
            var body = Assert.IsType<ScrollViewer>(dialog.Content);
            body.Measure(new Size(300, 300));
            body.Arrange(new Rect(0, 0, 300, 300));
            body.UpdateLayout();
            Assert.True(body.ExtentHeight > body.ViewportHeight && body.ViewportHeight > 0);
            Assert.True(body.ExtentWidth <= body.ViewportWidth + 1);
        }
        finally { dialog?.Close(); owner.Close(); }
    });

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) { yield return descendant; }
        }
    }
}
