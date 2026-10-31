using Microsoft.Win32;

namespace QuotaWidget.App;

public static class StartupRegistration
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string Command(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains('"')) { throw new ArgumentException("Invalid executable path.", nameof(executable)); }
        return "\"" + executable + "\"";
    }

    public static bool IsEnabled(string subKey = RunSubKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue("QuotaWidget") is not null;
    }

    public static void SetEnabled(bool enabled, string executable, string subKey = RunSubKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (enabled) { key.SetValue("QuotaWidget", Command(executable), RegistryValueKind.String); }
        else { key.DeleteValue("QuotaWidget", false); }
    }
}
