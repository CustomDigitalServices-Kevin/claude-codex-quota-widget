namespace QuotaWidget.App;

public partial class MainWindow
{
    internal Task TogglePackagingDetailsAsync() => ToggleSessionDetailsAsync("claude");
}
