namespace QuotaWidget.App;

public sealed record IconPresentation(string Text, string? Color);

public sealed record CardHeader(string Title, DateTimeOffset? CollectedAt, string CollectionText, string AgeText);

public sealed record WindowPresentation(string? Counter, string CounterLabel, string Type, string Label,
    string ShortLabel, double? Percent, string PercentText, string? Color, double? ElapsedFraction,
    string ResetText, string AgeText, double? AgeSeconds, double? PaceDifference, string PaceToolTip)
{
    public bool IsStale => AgeSeconds > 30 * 60;
    public string AccessibleText => $"{Label} : {PercentText}, {ResetText}, {AgeText}"
        + (PaceToolTip.Length > 0 ? ", " + PaceToolTip : "");
}

public sealed record ProviderPresentation(string Provider, string Name, string Plan, string Badge,
    string? Color, string AgeText, WindowPresentation[] Windows, string CreditsText,
    string[] Messages, string StatusText, string Details)
{
    public string HeaderToolTip => string.Join('\n', new[] { AgeText, CreditsText, Details }.Where(s => s.Length > 0).Concat(Messages));
}

public sealed record CardPresentation(CardHeader Header, ProviderPresentation[] Sections,
    string ToolTip, IconPresentation Icon)
{
    // A complete text alternative is derived from the same data as every visual mode.
    // Source limitations intentionally live only in README, including for accessibility.
    public string AccessibleText => string.Join('\n', new[] { Header.Title + " · " + Header.CollectionText, Header.AgeText }
        .Concat(Sections.SelectMany(section => new[] { section.Name + " " + section.Plan + " · " + section.Badge }
            .Concat(section.Windows.Select(window => window.AccessibleText))
            .Concat(section.Messages).Append(section.HeaderToolTip))));
}
