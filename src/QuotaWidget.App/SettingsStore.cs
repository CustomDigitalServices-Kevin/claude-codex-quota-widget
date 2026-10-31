using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed record WidgetSettings(double? X = null, double? Y = null, bool Topmost = true, bool Visible = true,
    DisplayMode Mode = DisplayMode.Detailed, ThemeChoice Theme = ThemeChoice.System, ModeSizes? Sizes = null,
    CardPreference[]? Cards = null)
{
    [JsonIgnore]
    public CardSize Size => (Sizes ?? new ModeSizes()).For(Mode);

    public bool Equals(WidgetSettings? other) =>
        other is not null
        && X == other.X
        && Y == other.Y
        && Topmost == other.Topmost
        && Visible == other.Visible
        && Mode == other.Mode
        && Theme == other.Theme
        && Equals(Sizes, other.Sizes)
        && CardPreferences.Normalize(Cards).SequenceEqual(CardPreferences.Normalize(other.Cards));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Topmost);
        hash.Add(Visible);
        hash.Add(Mode);
        hash.Add(Theme);
        hash.Add(Sizes);
        foreach (var card in CardPreferences.Normalize(Cards))
        {
            hash.Add(card);
        }
        return hash.ToHashCode();
    }
}

public sealed class SettingsStore(string path, WidgetLog log)
{
    public WidgetSettings Load()
    {
        if (!File.Exists(path))
        {
            log.Record(WidgetEvent.SettingsMissing);
            return new();
        }
        try
        {
            var settings = AtomicJsonFile.Read<WidgetSettings>(path);
            if (settings is null || settings.X.HasValue != settings.Y.HasValue
                || settings.X is { } x && !double.IsFinite(x) || settings.Y is { } y && !double.IsFinite(y)
                || !Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Theme) || settings.Sizes?.IsValid == false)
            {
                log.Record(WidgetEvent.SettingsInvalid);
                return new();
            }
            return settings with { Cards = CardPreferences.Normalize(settings.Cards) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Record(WidgetEvent.SettingsInvalid, exception: exception);
            return new();
        }
    }

    public void Save(WidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicJsonFile.Write(path, settings with { Cards = CardPreferences.Normalize(settings.Cards) });
        log.Record(WidgetEvent.SettingsSaved);
    }
}
