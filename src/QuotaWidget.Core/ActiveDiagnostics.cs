using System.Globalization;

namespace QuotaWidget.Core;

public static class ActiveDiagnostics
{
    public const string LoginCommand = "$env:CODEX_HOME = \"$env:LOCALAPPDATA\\QuotaWidget\\codex-home\"; codex login; Remove-Item Env:CODEX_HOME";

    public static string Format(Diagnostic diagnostic, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        zone ??= TimeZoneInfo.Local;
        return diagnostic.Code switch
        {
            "codex_introuvable" => "Exécutable natif Codex introuvable.",
            "profil_partage_refuse" => "Lecture directe refusée : profil Codex partagé.",
            "profil_dedie_non_connecte" => "Profil Codex dédié non connecté. Exécuter : " + LoginCommand,
            "etat_connexion_inconnu" => "État de connexion du profil Codex dédié inconnu.",
            "authentification_refusee" => "Lecture Codex suspendue : authentification refusée. Exécuter : " + LoginCommand,
            "lecture_directe_occupee" => "Lecture Codex directe occupée par un autre appelant.",
            "lecture_directe_trop_recente" => string.Create(CultureInfo.GetCultureInfo("fr-FR"),
                $"Dernière lecture directe il y a {Math.Floor(Math.Max(0, (now - (diagnostic.LastAttemptAt ?? now)).TotalMinutes)):0} min ; prochaine possible à {(diagnostic.NextAttemptAt is { } next ? TimeZoneInfo.ConvertTime(next, zone).ToString("HH:mm", CultureInfo.GetCultureInfo("fr-FR")) : "heure inconnue")}"),
            "lecture_directe_delai_depasse" => "Lecture Codex directe : délai dépassé.",
            "lecture_directe_annulee" => "Lecture Codex directe annulée.",
            "etat_lecture_directe_illisible" => "État de lecture Codex directe illisible ; lecture différée.",
            _ => "Échec de la lecture Codex directe ; nouvelle tentative différée.",
        };
    }
}
