using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Cli;

public static class QuotasCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error,
        TimeProvider? clock = null, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var options = CommandOptions.Parse(args);
            var effectiveClock = options.Now is { } frozen ? new FixedTimeProvider(frozen) : clock ?? TimeProvider.System;
            var now = effectiveClock.GetUtcNow();
            List<Diagnostic> diagnostics = [];
            var snapshotPath = Path.Combine(options.DataDirectory, "snapshot.json");
            var snapshot = SnapshotStore.Read(snapshotPath, diagnostics);
            var fresh = snapshot is not null && now >= snapshot.WrittenAt
                && now - snapshot.WrittenAt < TimeSpan.FromMinutes(2);
            var sourceName = fresh ? "instantane" : "collecte_passive";
            if (!fresh || options.Live)
            {
                var active = options.Live ? new CodexActiveSource(new(options.DataDirectory, options.CodexExecutablePath), effectiveClock) : null;
                snapshot = new QuotaCollector(effectiveClock, active).Collect(options.ClaudeDirectory, options.CodexProfiles, snapshot);
                var activeIssue = snapshot.Diagnostics.FirstOrDefault(d => d.Source == "app-server"
                    && d.Code != "derniere_mesure_conservee" && d.Code != WindowAggregator.ReplacedDiagnosticCode);
                sourceName = options.Live && activeIssue is null
                    ? "collecte_directe" : "collecte_passive";
                SnapshotStore.Write(snapshotPath, snapshot);
                if (options.Live && activeIssue is not null)
                { error.WriteLine(ActiveDiagnostics.Format(activeIssue, effectiveClock.GetUtcNow(), timeZone)); }
            }
            if (snapshot is null)
            {
                throw new InvalidDataException("A snapshot must be available after collection.");
            }
            // Recompute replaced-window diagnostics from raw observations and cached snapshots.
            diagnostics.AddRange(snapshot.Diagnostics.Where(d => d.Code != WindowAggregator.ReplacedDiagnosticCode));
            var state = StateBuilder.Build(snapshot.Observations, snapshot.Blockages, diagnostics);
            if (options.Arbitration)
            {
                var historyPath = Path.Combine(options.DataDirectory, "arbitrage-etat.json");
                var previous = ReadHistory(historyPath);
                var result = Arbitrator.Decide(state.Claude, state.Codex, previous, now);
                AtomicJsonFile.Write(historyPath, new ArbitrationHistory(result.Verdict, result.Step, now));
                output.WriteLine(options.Json ? OutputFormatter.ArbitrationJson(result) : result.Reason);
            }
            else
            {
                output.WriteLine(options.Json
                    ? OutputFormatter.QuotasJson(state, now, sourceName, [.. diagnostics])
                    : OutputFormatter.Text(state, now, timeZone ?? TimeZoneInfo.Local, [.. diagnostics]));
            }
            return 0;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"Erreur : {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                        or NotSupportedException or System.Security.SecurityException)
        {
            error.WriteLine("Erreur : lecture ou écriture des données impossible.");
            return 1;
        }
    }

    private static ArbitrationHistory? ReadHistory(string path)
    {
        try
        {
            return File.Exists(path) ? AtomicJsonFile.Read<ArbitrationHistory>(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
