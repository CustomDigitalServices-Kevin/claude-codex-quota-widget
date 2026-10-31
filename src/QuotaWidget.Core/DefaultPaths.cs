namespace QuotaWidget.Core;

public static class DefaultPaths
{
    public static string ClaudeDirectory => Path.Combine(Home, ".claude", "state", "claude-quotas.d");
    // Read standard local profiles and the widget's isolated optional profile.
    public static string[] CodexProfiles => [Path.Combine(Home, ".codex", "sessions"), Path.Combine(Home, ".codex-cli", "sessions"),
        Path.Combine(DataDirectory, "codex-home", "sessions")];
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaWidget");
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
