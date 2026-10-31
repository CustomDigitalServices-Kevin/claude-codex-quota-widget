using System.Globalization;

namespace QuotaWidget.Core;

public static class QuotaCalculations
{
    public static WindowMetrics Calculate(WindowObservation window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        var passed = window.ResetsAt is { } reset && reset <= now;
        double? elapsed = window.DurationMinutes is > 0 && window.ResetsAt is { } end
            ? Math.Clamp((window.DurationMinutes.Value - (end - now).TotalMinutes) / window.DurationMinutes.Value, 0, 1)
            : null;
        double? difference = elapsed is { } fraction && window.UsedPercent is { } used
            ? used - 100 * fraction : null;
        string? color = null;
        if (window.UsedPercent is { } percent)
        {
            if (elapsed is null or < 0.1)
            {
                color = percent < 60 ? "vert" : percent < 85 ? "orange" : "rouge";
            }
            else
            {
                var projection = percent / elapsed.Value;
                color = percent >= 90 || projection > 110 ? "rouge" : projection < 90 ? "vert" : "orange";
            }
        }
        return new(elapsed, difference, color, Age(window.MeasuredAt, now), passed);
    }

    public static double? RemainingFraction(WindowObservation window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.DurationMinutes is > 0 && window.ResetsAt is { } reset
            ? Math.Clamp((reset - now).TotalMinutes / window.DurationMinutes.Value, 0, 1) : null;
    }

    public static double? Age(DateTimeOffset? measuredAt, DateTimeOffset now) => measuredAt is { } measured
        ? Math.Max(0, (now - measured).TotalSeconds) : null;
}

public static class Arbitrator
{
    public static ArbitrationResult Decide(ProviderState claude, ProviderState codex,
        ArbitrationHistory? previous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(codex);
        var (c, uncertainC) = Numbers(claude, now);
        var (g, uncertainG) = Numbers(codex, now);
        var uncertain = uncertainC || uncertainG;
        var denominator = Math.Max(c.Reserve, g.Reserve);
        var difference = denominator > 0 ? Math.Abs(c.Reserve - g.Reserve) / denominator : 0;
        string verdict;
        int step;
        string decision;
        if (!c.Eligible || !g.Eligible)
        {
            verdict = c.Eligible ? "claude" : g.Eligible ? "gpt" : "aucun";
            step = 1;
            decision = verdict switch
            {
                "claude" => "exécution par Claude, Codex exclu (blocage ou fenêtre à 90 %)",
                "gpt" => "exécution déléguée à GPT, Claude exclu (blocage ou fenêtre à 90 %)",
                _ => "aucun fournisseur éligible (blocage ou fenêtre à 90 %)",
            };
        }
        else if (c.UsedFiveHour >= 85)
        {
            verdict = "gpt";
            step = 2;
            decision = string.Create(CultureInfo.GetCultureInfo("fr-FR"),
                $"exécution déléguée à GPT, urgence Claude 5 h à {c.UsedFiveHour:0.#} %");
        }
        else
        {
            var threshold = 0.25;
            if (previous is { Step: 3 or 4, Verdict: "claude" or "gpt" }
                && now >= previous.At && now - previous.At < TimeSpan.FromHours(24))
            {
                threshold = 0.20;
            }
            var balanced = difference < threshold;
            verdict = balanced ? "equilibre" : c.Reserve > g.Reserve ? "claude" : "gpt";
            step = balanced ? 4 : 3;
            decision = verdict switch
            {
                "claude" => "exécution par Claude",
                "gpt" => "exécution déléguée à GPT",
                _ => "équilibre, travail lourd délégué à GPT",
            };
        }
        var reason = string.Create(CultureInfo.GetCultureInfo("fr-FR"),
            $"Claude {WindowLabel(claude)} {c.Used:0.#} % (réserve {c.Reserve:0.#}), Codex {WindowLabel(codex)} {g.Used:0.#} % (réserve {g.Reserve:0.#}), écart {difference * 100:0.#} % : {decision}");
        if (uncertain)
        {
            reason += " ; incertain (mesure ancienne, inconnue, incomplète ou remise passée)";
        }
        return new(verdict, uncertain, step, reason, difference, c, g);
    }

    private static (ArbitrationNumbers Numbers, bool Uncertain) Numbers(ProviderState provider, DateTimeOffset now)
    {
        var window = Weekly(provider, now);
        var eligible = provider.Blockage.State != "bloque" && provider.Windows.All(w => EffectiveUsed(w, now) < 90);
        var used = window is null ? 0 : EffectiveUsed(window, now);
        var remaining = window is null ? null : QuotaCalculations.RemainingFraction(window, now);
        var reserve = (100 - used) / Math.Max(remaining ?? 1, 0.05);
        double? five = provider.Provider == "claude"
            ? provider.Windows.Where(w => w.Type == "five_hour" && w.UsedPercent is not null)
                .Select(w => (double?)EffectiveUsed(w, now)).Max()
            : null;
        var age = window is null ? null : QuotaCalculations.Age(window.MeasuredAt, now);
        var uncertain = window is null || provider.Windows.Any(w =>
            w.UsedPercent is null || w.MeasuredAt is null || w.DurationMinutes is null or <= 0
            || w.ResetsAt is null || w.ResetsAt <= now || QuotaCalculations.Age(w.MeasuredAt, now) > 21600);
        return (new(eligible, used, remaining, reserve, five, age), uncertain);
    }

    private static WindowObservation? Weekly(ProviderState provider, DateTimeOffset now)
    {
        var windows = provider.Provider == "claude"
            ? provider.Windows.Where(w => w.Type == "seven_day") : provider.Windows;
        return windows.Where(w => w.UsedPercent is not null).OrderByDescending(w => w.DurationMinutes == 10080)
            .ThenByDescending(w => w.DurationMinutes).ThenByDescending(w => EffectiveUsed(w, now))
            .ThenByDescending(w => w.MeasuredAt).FirstOrDefault();
    }

    private static double EffectiveUsed(WindowObservation window, DateTimeOffset now) =>
        window.ResetsAt <= now ? 0 : Math.Clamp(window.UsedPercent ?? 0, 0, 100);

    private static string WindowLabel(ProviderState provider)
    {
        var duration = provider.Provider == "claude" ? 10080
            : provider.Windows.OrderByDescending(w => w.DurationMinutes == 10080)
                .ThenByDescending(w => w.DurationMinutes).FirstOrDefault()?.DurationMinutes;
        return duration switch
        {
            10080 => "7 j",
            300 => "5 h",
            null => "durée inconnue",
            _ => string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{duration} min"),
        };
    }
}
