using System.Windows;

namespace QuotaWidget.App;

public partial class App
{
    private SettingsWindow? optionsWindow;

    private void OpenSettings()
    {
        if (card is null || quitting) { return; }
        if (!settings.Visible) { SetVisible(true); }
        if (optionsWindow is not null)
        {
            if (optionsWindow.WindowState == WindowState.Minimized) { optionsWindow.WindowState = WindowState.Normal; }
            optionsWindow.Activate();
            return;
        }
        optionsWindow = new SettingsWindow(card, settings, updated =>
        {
            settings = settings with { Theme = updated.Theme, Cards = CardPreferences.Normalize(updated.Cards) };
            card.ApplySettings(settings);
            if (log is not null) { card.ApplyTheme(settings.Theme, log); }
            Render();
            Revalidate(false);
            SaveSettings();
        });
        optionsWindow.Closed += (_, _) => optionsWindow = null;
        optionsWindow.Show();
    }

}
