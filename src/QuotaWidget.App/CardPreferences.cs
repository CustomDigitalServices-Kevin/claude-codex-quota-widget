using System.Text.Json.Serialization;

namespace QuotaWidget.App;

public sealed record CardPreference(
    string Id,
    bool Visible = true,
    string? Design = null,
    string? Theme = null)
{
    [JsonIgnore]
    public DisplayMode? DesignChoice => ParseDefined<DisplayMode>(Design);

    [JsonIgnore]
    public ThemeChoice? ThemeChoice => ParseDefined<ThemeChoice>(Theme);

    private static TEnum? ParseDefined<TEnum>(string? value) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse<TEnum>(value, true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : null;
}

public static class CardPreferences
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Pc = "pc";

    private static readonly string[] DefaultOrder = [Claude, Codex, Pc];

    public static CardPreference[] Normalize(CardPreference[]? preferences)
    {
        var normalized = new List<CardPreference>(DefaultOrder.Length);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preference in preferences ?? [])
        {
            if (preference is null || !TryCanonicalId(preference.Id, out var id) || !included.Add(id))
            {
                continue;
            }

            normalized.Add(new(
                id,
                preference.Visible,
                CanonicalName(preference.DesignChoice),
                CanonicalName(preference.ThemeChoice)));
        }

        foreach (var id in DefaultOrder)
        {
            if (included.Add(id))
            {
                normalized.Add(new(id));
            }
        }

        return [.. normalized];
    }

    public static CardPreference[] SetVisible(CardPreference[]? preferences, string id, bool visible) =>
        Update(preferences, id, preference => preference with { Visible = visible });

    public static CardPreference[] SetDesign(CardPreference[]? preferences, string id, DisplayMode? design) =>
        Update(preferences, id, preference => preference with { Design = CanonicalName(design) });

    public static CardPreference[] SetTheme(CardPreference[]? preferences, string id, ThemeChoice? theme) =>
        Update(preferences, id, preference => preference with { Theme = CanonicalName(theme) });

    public static CardPreference[] Move(CardPreference[]? preferences, string id, int delta)
    {
        var normalized = Normalize(preferences);
        if (!TryCanonicalId(id, out var canonicalId))
        {
            return normalized;
        }

        var current = Array.FindIndex(normalized,
            preference => string.Equals(preference.Id, canonicalId, StringComparison.Ordinal));
        if (current < 0 || delta == 0)
        {
            return normalized;
        }

        var target = Math.Clamp((long)current + delta, 0, normalized.Length - 1);
        if (target == current)
        {
            return normalized;
        }

        var moved = normalized[current];
        var list = normalized.ToList();
        list.RemoveAt(current);
        list.Insert((int)target, moved);
        return [.. list];
    }

    private static CardPreference[] Update(
        CardPreference[]? preferences,
        string id,
        Func<CardPreference, CardPreference> update)
    {
        var normalized = Normalize(preferences);
        if (!TryCanonicalId(id, out var canonicalId))
        {
            return normalized;
        }

        var index = Array.FindIndex(normalized,
            preference => string.Equals(preference.Id, canonicalId, StringComparison.Ordinal));
        if (index >= 0)
        {
            normalized[index] = update(normalized[index]);
        }
        return normalized;
    }

    private static bool TryCanonicalId(string? value, out string id)
    {
        id = DefaultOrder.FirstOrDefault(candidate =>
            string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return id.Length > 0;
    }

    private static string? CanonicalName<TEnum>(TEnum? value) where TEnum : struct, Enum =>
        value?.ToString();
}
