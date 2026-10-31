using System.Globalization;
using System.Text.RegularExpressions;

namespace QuotaWidget.App;

internal sealed partial record SessionRowPresentation(string Title, string? Agent, string? Model, string? Effort,
    string Time, bool HasDetails)
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    internal static SessionRowPresentation From(SessionDetails detail, DateTimeOffset now)
    {
        var name = HumanText(detail.Name, detail.Id);
        var title = name ?? (detail.Provider == "claude" ? "Session Claude" : "Session Codex");
        var nickname = HumanText(detail.AgentNickname, detail.Id);
        var role = Role(detail.AgentRole, detail.Id);
        var agent = nickname is null ? role : role is null ? nickname : nickname + " · " + role;
        var date = detail.CreatedAt ?? detail.ObservedAt;
        var local = date.ToLocalTime();
        var time = (detail.CreatedAt is null ? "Observée" : "Créée")
            + (local.Date == now.ToLocalTime().Date ? " à " : " le " + local.ToString("dd/MM", French) + " à ")
            + local.ToString("HH:mm", French);
        var model = ModelName(HumanText(detail.Model, detail.Id));
        var effort = EffortName(detail.Effort);
        return new(title, agent, model, effort, time,
            name is not null || agent is not null || model is not null || effort is not null || detail.CreatedAt is not null);
    }

    private static string? HumanText(string? value, string id)
    {
        if (value is null) { return null; }
        var cleaned = id.Length == 0 ? value : value.Replace(id, "", StringComparison.OrdinalIgnoreCase);
        cleaned = IdentifierPattern().Replace(cleaned, "");
        var text = SessionMetadataReader.Sanitize(cleaned, 180);
        if (text is null || Guid.TryParse(text, out _)
            || text.Length >= 16 && text.All(char.IsAsciiHexDigit)) { return null; }
        return text;
    }

    private static string? Role(string? value, string id) => value?.ToLowerInvariant() switch
    {
        "reviewer" => "Relecture", "explorer" => "Exploration", "worker" => "Exécution",
        "advisor" => "Conseil", "default" => null, _ => HumanText(value, id)?.Replace('_', ' '),
    };

    private static string? EffortName(string? value) => value?.ToLowerInvariant() switch
    {
        "none" => "Sans raisonnement", "minimal" => "Effort minimal", "low" => "Effort faible",
        "medium" => "Effort moyen", "high" => "Effort élevé", "xhigh" => "Effort très élevé",
        "max" => "Effort maximal", _ => null,
    };

    private static string? ModelName(string? value)
    {
        var model = HumanText(value, string.Empty);
        if (model is null) { return null; }
        var parts = model.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var version = string.Join('.', parts.Skip(2).TakeWhile(part => part.Length <= 2 && part.All(char.IsAsciiDigit)));
            return "Claude " + French.TextInfo.ToTitleCase(parts[1]) + (version.Length == 0 ? "" : " " + version);
        }
        if (parts.Length >= 2 && parts[0].Equals("gpt", StringComparison.OrdinalIgnoreCase))
        { return "GPT " + French.TextInfo.ToTitleCase(string.Join(' ', parts.Skip(1))); }
        return French.TextInfo.ToTitleCase(model.Replace('-', ' ').Replace('_', ' '));
    }

    [GeneratedRegex(@"\b(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{16,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
