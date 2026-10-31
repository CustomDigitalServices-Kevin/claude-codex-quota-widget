namespace QuotaWidget.App;

public static class CardVisualPolicy
{
    public static bool UsesGauges(DisplayMode mode, int windowCount) =>
        mode == DisplayMode.Gauges || (mode == DisplayMode.Detailed && windowCount > 1);

    public static string ProviderAccent(string provider) => provider switch
    {
        "claude" => "ClaudeAccent",
        "codex" => "CodexAccent",
        _ => "BlueAccent",
    };

    public static string ProviderBorder(string provider) => provider switch
    {
        "claude" => "ClaudeBorder",
        "codex" => "CodexBorder",
        _ => "NeutralBorder",
    };
}
