namespace QuotaWidget.App.Tests;

// The application relies on this contract: a close the user asks for hides the card (and is saved as hidden),
// a close the application allows (Quit, end of the Windows session) closes it without any hide request.
[Collection("WPF rendering")]
public sealed class CardCloseTests
{
    [Fact]
    public void UserCloseHidesTheCardInsteadOfClosingIt() => WidgetRenderingTests.RunSta(() =>
    {
        var window = new MainWindow { Left = -10000, Top = -10000 };
        var hideRequests = 0;
        var closed = false;
        window.HideRequested += (_, _) => hideRequests++;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            window.Close();
            Assert.Equal(1, hideRequests);
            Assert.False(closed);
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
        }
    });

    [Fact]
    public void AllowedCloseClosesTheCardWithoutHideRequest() => WidgetRenderingTests.RunSta(() =>
    {
        var window = new MainWindow { Left = -10000, Top = -10000 };
        var hideRequests = 0;
        var closed = false;
        window.HideRequested += (_, _) => hideRequests++;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.AllowClose = true;
        window.Close();
        Assert.Equal(0, hideRequests);
        Assert.True(closed);
    });
}
