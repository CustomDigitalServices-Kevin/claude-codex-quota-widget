using System.Globalization;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public static class CardFormatter
{
    // .NET 10 NotifyIcon measures UTF-16 length, up to 127 characters.
    public const int ToolTipLimit = 127;

    public static CardPresentation Format(QuotaState state, IEnumerable<Diagnostic> diagnostics,
        DateTimeOffset now, DateTimeOffset? collectedAt = null, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.GetCultureInfo("fr-FR");
        var issues = diagnostics.ToArray();
        var sections = new[] { state.Claude, state.Codex }
            .Select(provider => Section(provider, issues, now, zone, culture,
                provider.Provider == "codex" && state.OtherAccounts.Length > 0)).ToArray();
        var header = new CardHeader("Quotas IA", collectedAt,
            collectedAt is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", culture) : "en attente",
            OldestAge(state.Claude.Windows.Concat(state.Codex.Windows), now, culture));
        var summary = sections.SelectMany(section => section.Windows.Length == 0
            ? new[] { section.Name.ToUpperInvariant() + " : aucune mesure" }
            : section.Windows.Select(window => section.Name.ToUpperInvariant() + window.ShortLabel[section.Name.Length..]
                + " " + window.PercentText));
        return new(header, sections, TruncateToolTip(string.Join('\n', summary)), SelectIcon(state, now));
    }

    private static ProviderPresentation Section(ProviderState provider, Diagnostic[] issues, DateTimeOffset now,
        TimeZoneInfo zone, CultureInfo culture, bool otherAccounts)
    {
        var name = provider.Provider == "claude" ? "Claude" : "Codex";
        var windows = provider.Windows.OrderBy(w => w.DurationMinutes).ThenBy(w => w.Counter, StringComparer.Ordinal)
            .ThenBy(w => w.Type, StringComparer.Ordinal).Select(window =>
            {
                var metrics = QuotaCalculations.Calculate(window, now);
                var counter = CounterLabel(window.Counter);
                // Even a single model-specific counter must be named. Do not merge equal durations.
                var suffix = counter.Length > 0 && !string.Equals(counter, name, StringComparison.OrdinalIgnoreCase)
                    ? " · " + counter : "";
                if (provider.Windows.Count(w => w.Counter == window.Counter && w.DurationMinutes == window.DurationMinutes) > 1)
                { suffix += " · " + TypeLabel(window.Type); }
                return new WindowPresentation(window.Counter, counter, window.Type, WindowLabel(window.DurationMinutes, culture) + suffix,
                    name + " " + StatusLineFormatter.DurationLabel(window.DurationMinutes) + suffix,
                    window.UsedPercent, window.UsedPercent is { } used
                        ? Math.Round(used, MidpointRounding.AwayFromZero).ToString("0", culture) + " %" : "? %",
                    metrics.Color, metrics.ElapsedFraction, ResetText(window.ResetsAt, now, zone, culture),
                    AgeText(window.MeasuredAt, now, culture), metrics.AgeSeconds, metrics.PaceDifference,
                    metrics.ElapsedFraction is { } elapsed
                        ? "Rythme linéaire : " + (elapsed * 100).ToString("0.#", culture) + " % écoulés"
                            + (metrics.PaceDifference is { } pace ? " ; écart : " + pace.ToString("+0.#;-0.#;0", culture) + " pts" : "") : "");
            }).ToArray();
        List<string> messages = [];
        if (windows.Length == 0) { messages.Add("Aucune mesure — " + MissingReason(provider.Provider, issues)); }
        if (provider.Blockage.State == "bloque")
        {
            var date = provider.Blockage.Since is { } since
                ? TimeZoneInfo.ConvertTime(since, zone).ToString("ddd dd/MM HH:mm", culture) : "date inconnue";
            messages.Add(provider.Windows.Any(w => w.UsedPercent >= 100 && w.ResetsAt > now)
                ? "Bloqué depuis " + date : "Blocage constaté le " + date + ", rétablissement non confirmé");
        }
        if (provider.Provider == "codex")
        {
            foreach (var code in issues.Select(d => d.Code).Distinct(StringComparer.Ordinal))
            {
                var message = code switch
                {
                    "authentification_refusee" => "Lecture Codex en direct suspendue : authentification refusée",
                    "profil_dedie_non_connecte" => "Profil Codex non connecté",
                    "profil_partage_refuse" => "Lecture directe refusée : profil partagé",
                    "codex_introuvable" => "Exécutable Codex introuvable",
                    "etat_connexion_inconnu" => "Connexion Codex inconnue",
                    "lecture_directe_delai_depasse" => "Lecture directe : délai dépassé",
                    "erreur_lecture_directe" => "Échec de lecture directe",
                    "etat_lecture_directe_illisible" => "Lecture directe différée : état illisible",
                    _ => null,
                };
                if (message is not null) { messages.Add(message); }
            }
        }
        var color = provider.Blockage.State == "bloque" ? "rouge"
            : windows.Any(w => w.Color == "rouge") ? "rouge" : windows.Any(w => w.Color == "orange") ? "orange"
            : windows.Any(w => w.Color == "vert") ? "vert" : null;
        var badge = provider.Blockage.State == "bloque" ? "Bloqué" : color switch
        { "rouge" => "Critique", "orange" => "À surveiller", "vert" => "OK", _ => "Aucune mesure" };
        var status = provider.Blockage.State == "bloque"
            ? provider.Windows.Any(w => w.UsedPercent >= 100 && w.ResetsAt > now) ? "Bloqué" : "Bloqué, rétablissement non confirmé"
            : messages.LastOrDefault() switch
            {
                "Lecture Codex en direct suspendue : authentification refusée" => "Lecture directe refusée — authentification",
                { } message when message.StartsWith("Aucune mesure", StringComparison.Ordinal) => "Aucune mesure",
                { } message => message,
                _ => "",
            };
        return new(provider.Provider, name, PlanLabel(provider.Plan), badge, color,
            OldestAge(provider.Windows, now, culture), windows,
            provider.Provider == "codex" ? CreditsText(provider, now, zone, culture) : "", [.. messages], status,
            otherAccounts ? "Autres comptes Codex : mesures séparées consultables avec quotas --json" : "");
    }

    public static string CounterLabel(string? counter) => counter switch
    {
        null or "" => "",
        "claude" => "Claude",
        "codex" => "Codex",
        _ => counter.Replace('_', ' '),
    };

    private static string TypeLabel(string type) => type switch
    { "primary" => "principale", "secondary" => "secondaire", _ => type.Replace('_', ' ') };

    private static string PlanLabel(string? plan) => string.IsNullOrWhiteSpace(plan) ? "" : plan.ToLowerInvariant() switch
    { "pro" => "Pro", "max" => "Max", "plus" => "Plus", "free" => "Free", "team" => "Team", "business" => "Business", _ => plan };

    private static string OldestAge(IEnumerable<WindowObservation> source, DateTimeOffset now, CultureInfo culture)
    {
        var windows = source.ToArray();
        // An undated observation must not be represented by another window's fresh timestamp.
        return windows.Length == 0 || windows.Any(w => w.MeasuredAt is null)
            ? "âge inconnu" : AgeText(windows.Min(w => w.MeasuredAt), now, culture);
    }

    private static string CreditsText(ProviderState provider, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var credits = provider.Credits?.Unlimited == true ? "illimités"
            : provider.Credits?.Balance?.ToString("0.##", culture);
        if (provider.ActiveMeasuredAt is not { } activeAt)
        {
            return credits is null ? "Crédits et remises gratuites : inconnu (lecture directe requise)"
                : "Crédits : " + credits + "; remises gratuites : inconnu (lecture directe requise)";
        }
        var count = provider.ResetCredits?.AvailableCount?.ToString(culture) ?? "inconnu";
        var validity = provider.ResetCredits?.Credits is { Length: > 0 } rows
            ? "; validité : " + string.Join(", ", rows.Select(row => row.ExpiresAt is { } expires
                ? TimeZoneInfo.ConvertTime(expires, zone).ToString("dd/MM HH:mm", culture) : "sans expiration"))
            : provider.ResetCredits?.AvailableCount == 0 ? "" : "; validité : inconnue";
        return "Crédits : " + (credits ?? "inconnu") + "; remises gratuites : " + count + validity
            + " ; lecture directe il y a " + Math.Floor(Math.Max(0, (now - activeAt).TotalMinutes)).ToString("0", culture) + " min";
    }

    public static string WindowLabel(long? minutes, CultureInfo culture) => minutes switch
    {
        300 => "5 h glissantes",
        10080 => "Semaine",
        > 0 => "Fenêtre " + (minutes.Value / 60d).ToString("0.##", culture) + " h",
        _ => "Fenêtre inconnue",
    };

    public static string AgeText(DateTimeOffset? measured, DateTimeOffset now, CultureInfo culture) =>
        measured is null ? "âge inconnu" : "mesure il y a " + Math.Floor(Math.Max(0, (now - measured.Value).TotalMinutes)).ToString("0", culture) + " min";

    private static string ResetText(DateTimeOffset? reset, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (reset is null) { return "remise inconnue"; }
        if (reset <= now) { return "remise passée, en attente de mesure"; }
        var local = TimeZoneInfo.ConvertTime(reset.Value, zone);
        return "remise " + local.ToString(local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? "HH:mm" : "ddd HH:mm", culture);
    }

    private static string MissingReason(string provider, Diagnostic[] diagnostics)
    {
        var codes = diagnostics.Where(d => d.Code.StartsWith(provider + "_", StringComparison.Ordinal)).Select(d => d.Code).ToArray();
        if (codes.Any(c => c.EndsWith("dossier_illisible", StringComparison.Ordinal))) { return "dossier absent ou illisible"; }
        if (codes.Any(c => c.EndsWith("fichier_ignore", StringComparison.Ordinal))) { return "fichier illisible ou invalide"; }
        if (codes.Length > 0) { return "aucune mesure exploitable dans les fichiers lus"; }
        return "aucune source disponible pour ce fournisseur ou ce compte";
    }

    public static IconPresentation SelectIcon(QuotaState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Claude.Blockage.State == "bloque" || state.Codex.Blockage.State == "bloque") { return new("!", "rouge"); }
        var highest = state.Claude.Windows.Concat(state.Codex.Windows).Where(w => w.UsedPercent is not null)
            .OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        return highest is null ? new("?", null)
            : new(Math.Round(highest.UsedPercent!.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
                QuotaCalculations.Calculate(highest, now).Color);
    }

    public static string TruncateToolTip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= ToolTipLimit) { return text; }
        var elements = StringInfo.ParseCombiningCharacters(text);
        var end = elements.Last(i => i <= ToolTipLimit - 1);
        return text[..end] + "…";
    }
}
