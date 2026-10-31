using System.Globalization;

namespace QuotaWidget.Core;

// Shared with aggregation: fixed anchor, never a sliding chain of reset timestamps.
public sealed record WindowIdentity(string Provider, string? Counter, string Type, long? DurationMinutes)
{
    public static WindowIdentity From(WindowObservation window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new(window.Provider, window.Counter, window.Type, window.DurationMinutes);
    }

    public static bool SameResetCluster(DateTimeOffset? anchor, DateTimeOffset? reset) =>
        anchor is null ? reset is null : reset is { } at && Math.Abs((at - anchor.Value).TotalSeconds) <= 120;
}

public sealed record AlertThreshold(WindowIdentity Identity, DateTimeOffset? ResetAnchor, int Threshold);
public sealed record AlertState(AlertThreshold[] Thresholds, DateTimeOffset StartedAt,
    DateTimeOffset? LastClaudeMeasurement = null, bool ClaudeStaleEmitted = false);
public sealed record QuotaAlert(string Provider, WindowObservation? Window, int? Threshold)
{
    public string Text(TimeZoneInfo? zone = null)
    {
        if (Window is null) { return "Aucune mesure Claude depuis 6 h."; }
        var reset = Window.ResetsAt is { } at
            ? TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("ddd HH:mm", CultureInfo.GetCultureInfo("fr-FR"))
            : "inconnue";
        return string.Create(CultureInfo.GetCultureInfo("fr-FR"),
            $"{(Provider == "claude" ? "Claude" : "Codex")} · {StatusLineFormatter.DurationLabel(Window.DurationMinutes)} · {Window.UsedPercent:0.#} % · remise {reset}");
    }
}
public sealed record AlertEvaluation(AlertState State, QuotaAlert[] Alerts);

public static class AlertPolicy
{
    public static AlertEvaluation Evaluate(QuotaState quotas, AlertState? previous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quotas);
        var state = previous ?? new([], now);
        List<AlertThreshold> thresholds = [.. state.Thresholds];
        List<QuotaAlert> alerts = [];
        foreach (var (provider, window) in new[] { quotas.Claude, quotas.Codex }
                     .SelectMany(provider => provider.Windows.Select(window => (provider, window))))
        {
            if (window.MeasuredAt is not { } measured || now - measured > TimeSpan.FromMinutes(30)
                || measured > now || window.ResetsAt <= now || window.UsedPercent is null or < 80) { continue; }
            var identity = WindowIdentity.From(window);
            var cluster = provider.WindowClusters?.FirstOrDefault(c => c.Window == window);
            var anchor = cluster is null ? window.ResetsAt : cluster.ResetAnchor;
            var index = thresholds.FindIndex(t => t.Identity == identity && WindowIdentity.SameResetCluster(t.ResetAnchor, anchor));
            var highest = window.UsedPercent >= 95 ? 95 : 80;
            if (index >= 0 && thresholds[index].Threshold >= highest) { continue; }
            if (index >= 0) { thresholds[index] = thresholds[index] with { Threshold = highest }; }
            else { thresholds.Add(new(identity, anchor, highest)); }
            alerts.Add(new(window.Provider, window, highest));
        }
        var latestClaude = quotas.Claude.Windows.Select(w => w.MeasuredAt).Max();
        if (latestClaude is { } latest && (state.LastClaudeMeasurement is null || latest > state.LastClaudeMeasurement))
        { state = state with { LastClaudeMeasurement = latest, ClaudeStaleEmitted = false }; }
        if (!state.ClaudeStaleEmitted && now - (state.LastClaudeMeasurement ?? state.StartedAt) >= TimeSpan.FromHours(6))
        {
            alerts.Add(new("claude", null, null));
            state = state with { ClaudeStaleEmitted = true };
        }
        return new(state with { Thresholds = [.. thresholds] }, [.. alerts]);
    }
}
