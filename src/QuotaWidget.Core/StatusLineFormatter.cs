using System.Globalization;
using System.Text;

namespace QuotaWidget.Core;

public static class StatusLineFormatter
{
    public static string DurationLabel(long? minutes) => minutes switch
    {
        null or <= 0 => "durée inconnue",
        { } n when n % 1440 == 0 => string.Create(CultureInfo.InvariantCulture, $"{n / 1440} j"),
        { } n when n % 60 == 0 => string.Create(CultureInfo.InvariantCulture, $"{n / 60} h"),
        { } n => string.Create(CultureInfo.InvariantCulture, $"{n} min"),
    };

    public static string Format(QuotaState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);
        var output = new StringBuilder("v1\n");
        foreach (var window in state.Codex.Windows)
        {
            var reset = window.ResetsAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : (DateTimeOffset?)null;
            var format = reset?.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? "HH:mm" : "ddd HH:mm";
            output.Append("codex\t").Append(DurationLabel(window.DurationMinutes)).Append('\t')
                .Append(window.UsedPercent is { } used ? Math.Round(used, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "")
                .Append('\t').Append(window.ResetsAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(reset?.ToString(format, CultureInfo.GetCultureInfo("fr-FR")))
                .Append('\t').Append(window.MeasuredAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return output.ToString();
    }
}
