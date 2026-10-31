using System.Globalization;
using System.Text;
using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Cli;

public static class OutputFormatter
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] Limits =
    [
        "Plafond absolu non exposé par les sources",
        "Mesure Claude rafraîchie seulement par une session Claude Code interactive",
        "Messages ChatGPT (chat) : non exposés",
    ];

    public static string QuotasJson(QuotaState state, DateTimeOffset now, string source, Diagnostic[] diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return JsonSerializer.Serialize(new
        {
            version = 1,
            genere_a = now,
            source,
            claude = ProviderJson(state.Claude, now, []),
            codex = ProviderJson(state.Codex, now, state.OtherAccounts),
            limites = Limits,
            diagnostics = diagnostics.Select(d => new { code = d.Code, source = d.Source, nombre = d.Count,
                prochaine_lecture_a = d.NextAttemptAt, derniere_tentative_a = d.LastAttemptAt }).ToArray(),
        }, JsonSupport.Options);
    }

    public static string ArbitrationJson(ArbitrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(new
        {
            verdict = result.Verdict,
            incertain = result.Uncertain,
            etape = result.Step,
            raison = result.Reason,
            ecart_relatif = result.RelativeDifference,
            claude = NumbersJson(result.Claude),
            codex = NumbersJson(result.Codex),
        }, JsonSupport.Options);
    }

    private static object NumbersJson(ArbitrationNumbers numbers) => new
    {
        eligible = numbers.Eligible,
        utilise = numbers.Used,
        fraction_restante = numbers.RemainingFraction,
        reserve = numbers.Reserve,
        utilise_5h = numbers.UsedFiveHour,
        age_s = numbers.AgeSeconds,
    };

    private static object ProviderJson(ProviderState provider, DateTimeOffset now, ProviderState[] others) => new
    {
        fenetres = provider.Windows.Select(w => WindowJson(w, now)).ToArray(),
        blocage = new { etat = provider.Blockage.State, depuis = provider.Blockage.Since, provenance = provider.Blockage.Provenance },
        compte = provider.Account,
        source_compte_inconnu = provider.UnknownSource,
        plan = provider.Plan,
        credits = provider.Credits is { } credits
            ? new { solde = credits.Balance, illimites = credits.Unlimited, disponibles = credits.HasCredits } : null,
        usage_ordinaire_autorise = provider.OrdinaryUsageAllowed,
        lecture_directe_a = provider.ActiveMeasuredAt,
        remises_gratuites = provider.ResetCredits is { } resets ? new
        {
            nombre_disponible = resets.AvailableCount,
            credits = resets.Credits?.Select(c => new
            {
                id = c.Id, type = c.ResetType, statut = c.Status, accorde_a = c.GrantedAt,
                expire_a = c.ExpiresAt, titre = c.Title, description = c.Description,
            }).ToArray(),
        } : null,
        autres_comptes = others.Select(other => ProviderJson(other, now, [])).ToArray(),
    };

    private static object WindowJson(WindowObservation window, DateTimeOffset now)
    {
        var metrics = QuotaCalculations.Calculate(window, now);
        return new
        {
            compteur = window.Counter,
            type = window.Type,
            utilise = window.UsedPercent,
            remise_a = window.ResetsAt,
            duree_min = window.DurationMinutes,
            fraction_ecoulee = metrics.ElapsedFraction,
            ecart_rythme = metrics.PaceDifference,
            couleur = metrics.Color,
            mesure_a = window.MeasuredAt,
            observe_a = window.ObservedAt,
            age_s = metrics.AgeSeconds,
            remise_passee = metrics.ResetPassed,
            source = window.Source,
        };
    }

    public static string Text(QuotaState state, DateTimeOffset now, TimeZoneInfo zone, Diagnostic[] diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(zone);
        var output = new StringBuilder();
        output.AppendLine(French, $"Quotas IA                                      {TimeZoneInfo.ConvertTime(now, zone).ToString("HH:mm", French)}");
        AppendProvider(output, "CLAUDE · quota partagé Code + claude.ai", state.Claude, now, zone);
        AppendProvider(output, "CODEX · abonnement ChatGPT", state.Codex, now, zone);
        foreach (var other in state.OtherAccounts)
        {
            AppendProvider(output, $"CODEX · autre compte {other.Account ?? "inconnu"}", other, now, zone);
        }
        foreach (var limit in Limits)
        {
            output.AppendLine(limit);
        }
        if (diagnostics.Length > 0)
        {
            output.AppendLine(string.Create(French, $"Diagnostics : {diagnostics.Sum(d => d.Count)} anomalie(s) de lecture."));
        }
        return output.ToString().TrimEnd();
    }

    private static void AppendProvider(StringBuilder output, string title, ProviderState provider,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        output.AppendLine(title);
        if (provider.Windows.Length == 0)
        {
            output.AppendLine(" Aucune mesure disponible");
        }
        foreach (var window in provider.Windows)
        {
            var metrics = QuotaCalculations.Calculate(window, now);
            var label = window.DurationMinutes switch
            {
                300 => "5 h glissantes",
                10080 => "Semaine",
                null => "Durée inconnue",
                _ => string.Create(French, $"{window.DurationMinutes} min"),
            };
            var used = window.UsedPercent is { } percent ? percent.ToString("0.#", French) + " %" : "utilisation inconnue";
            output.AppendLine(French, $" {label} [{window.Counter ?? "compteur inconnu"}] : {used} · {ResetText(window.ResetsAt, now, zone)} · {AgeText(metrics.AgeSeconds)} · {metrics.Color ?? "couleur inconnue"}");
            if (metrics.PaceDifference is { } difference)
            {
                output.AppendLine(French, $"  écart au rythme linéaire : {difference.ToString("+0.#;-0.#;0", French)} pts");
            }
        }
        if (provider.Blockage.State == "bloque")
        {
            var since = provider.Blockage.Since is { } date
                ? TimeZoneInfo.ConvertTime(date, zone).ToString("dd/MM HH:mm", French) : "date inconnue";
            output.AppendLine(French, $" Bloqué, blocage constaté le {since}, rétablissement non confirmé");
        }
        else
        {
            output.AppendLine(provider.Blockage.State == "non_bloque" ? " Non bloqué (mesure confirmée)" : " Blocage inconnu");
        }
        if (provider.Provider == "codex")
        {
            output.AppendLine(French, $" Compte : {provider.Account ?? "inconnu"} · Plan : {provider.Plan ?? "inconnu"}");
            if (provider.Credits is { } credits)
            {
                output.AppendLine(French, $" Crédits : {credits.Balance?.ToString("0.##", French) ?? "solde inconnu"}, illimités : {BooleanText(credits.Unlimited)}");
            }
            else
            {
                output.AppendLine(" Crédits : inconnus");
            }
            output.AppendLine(French, $" Usage ordinaire autorisé : {BooleanText(provider.OrdinaryUsageAllowed)}");
            output.AppendLine(provider.ResetCredits?.AvailableCount is { } count
                ? string.Create(French, $" Remises gratuites : {count}")
                : " Remises gratuites : inconnu (lecture directe requise)");
        }
    }

    private static string BooleanText(bool? value) => value switch { true => "oui", false => "non", _ => "inconnu" };

    public static string ResetText(DateTimeOffset? reset, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (reset is null)
        {
            return "remise inconnue";
        }
        if (reset <= now)
        {
            return "remise passée, en attente de mesure";
        }
        var local = TimeZoneInfo.ConvertTime(reset.Value, zone);
        var format = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? "HH:mm" : "ddd HH:mm";
        return $"remise {local.ToString(format, French)}";
    }

    public static string AgeText(double? seconds) => seconds switch
    {
        null => "mesure inconnue",
        < 60 => "mesure il y a moins d’une min",
        < 3600 => string.Create(French, $"mesure il y a {Math.Floor(seconds.Value / 60)} min"),
        < 86400 => string.Create(French, $"mesure il y a {Math.Floor(seconds.Value / 3600)} h"),
        _ => string.Create(French, $"mesure il y a {Math.Floor(seconds.Value / 86400)} j"),
    };
}
