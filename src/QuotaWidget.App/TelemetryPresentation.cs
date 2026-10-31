using System.Globalization;

namespace QuotaWidget.App;

public sealed record MachineMetric(string Label, string Value, string Detail, double? Percent, string ToolTip);

public static class TelemetryPresentation
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static MachineMetric[] Format(SystemTelemetrySnapshot? snapshot)
    {
        if (snapshot is null) { return [Loading("CPU"), Loading("RAM"), Loading("GPU"), Loading("VRAM")]; }
        var gpu = snapshot.Gpus.OrderByDescending(item => item.UtilizationPercent ?? -1)
            .ThenByDescending(item => item.DedicatedTotalBytes ?? 0).FirstOrDefault();
        var allGpus = snapshot.Gpus.Length == 0 ? snapshot.GpuIssue ?? "Compteurs GPU indisponibles"
            : string.Join('\n', snapshot.Gpus.Select(item => $"{item.Name} : {Percent(item.UtilizationPercent)}, VRAM {Memory(item.DedicatedUsedBytes, item.DedicatedTotalBytes)}, partagée {Bytes(item.SharedUsedBytes)}"));
        return
        [
            new("CPU", Percent(snapshot.CpuPercent), "tous les processeurs", snapshot.CpuPercent,
                snapshot.CpuIssue ?? "Utilisation totale entre deux relevés."),
            new("RAM", Percent(Ratio(snapshot.RamUsedBytes, snapshot.RamTotalBytes)), Memory(snapshot.RamUsedBytes, snapshot.RamTotalBytes),
                Ratio(snapshot.RamUsedBytes, snapshot.RamTotalBytes), snapshot.RamIssue ?? "Mémoire physique utilisée et totale."),
            new("GPU", Percent(gpu?.UtilizationPercent), gpu?.Name ?? "Aucun relevé", gpu?.UtilizationPercent,
                "GPU le plus sollicité, moteur le plus occupé.\n" + allGpus),
            new("VRAM", Bytes(gpu?.DedicatedUsedBytes), gpu?.DedicatedTotalBytes is { } total ? "sur " + Bytes(total) : "mémoire dédiée",
                Ratio(gpu?.DedicatedUsedBytes, gpu?.DedicatedTotalBytes), "Mémoire dédiée du GPU affiché. La mémoire partagée est distincte.\n" + allGpus),
        ];
    }

    public static string Percent(double? value) => value is { } number && double.IsFinite(number)
        ? Math.Clamp(number, 0, 100).ToString("0", French) + " %" : "Indisponible";

    public static string Bytes(ulong? value) => value is { } bytes ? (bytes / 1073741824d).ToString("0.0", French) + " Gio" : "Indisponible";

    private static string Memory(ulong? used, ulong? total) => used.HasValue && total.HasValue
        ? Bytes(used) + " / " + Bytes(total) : "Indisponible";

    private static double? Ratio(ulong? used, ulong? total) => used.HasValue && total is > 0
        ? Math.Clamp(used.Value / (double)total.Value * 100, 0, 100) : null;

    private static MachineMetric Loading(string label) => new(label, "…", "premier relevé", null, "Collecte système en cours.");
}
