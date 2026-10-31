namespace QuotaWidget.Core;

public static class WindowAggregator
{
    public const string ReplacedDiagnosticCode = "fenetre_remplacee_par_mesure_recente";

    public static WindowObservation[] Aggregate(IEnumerable<WindowObservation> observations)
        => Clusters(observations).Select(c => c.Window).ToArray();

    private static WindowCluster[] Clusters(IEnumerable<WindowObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        List<WindowCluster> result = [];
        foreach (var identity in observations.GroupBy(WindowIdentity.From)
                     .OrderBy(g => g.Key.Provider, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Counter, StringComparer.Ordinal).ThenBy(g => g.Key.Type, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.DurationMinutes))
        {
            List<WindowObservation> cluster = [];
            DateTimeOffset? anchor = null;
            foreach (var window in identity.OrderBy(w => w.ResetsAt))
            {
                var belongs = cluster.Count == 0 || WindowIdentity.SameResetCluster(anchor, window.ResetsAt);
                if (!belongs)
                {
                    result.Add(new(Select(cluster), anchor));
                    cluster.Clear();
                }
                if (cluster.Count == 0)
                {
                    anchor = window.ResetsAt;
                }
                cluster.Add(window);
            }
            if (cluster.Count > 0)
            {
                result.Add(new(Select(cluster), anchor));
            }
        }
        return [.. result];
    }

    // Call within one account only; unknown accounts remain isolated by source in StateBuilder.
    internal static WindowObservation[] Current(IEnumerable<WindowObservation> observations,
        ICollection<Diagnostic>? diagnostics)
        => CurrentClusters(observations, diagnostics).Select(c => c.Window).ToArray();

    internal static WindowCluster[] CurrentClusters(IEnumerable<WindowObservation> observations,
        ICollection<Diagnostic>? diagnostics)
    {
        List<WindowCluster> current = [];
        foreach (var counter in Clusters(observations).GroupBy(c => (c.Window.Provider, c.Window.Counter, c.Window.DurationMinutes)))
        {
            var selected = Select(counter.Select(c => c.Window));
            current.Add(counter.First(c => ReferenceEquals(c.Window, selected)));
            foreach (var replaced in counter.Where(c => !ReferenceEquals(c.Window, selected)))
            {
                diagnostics?.Add(new(ReplacedDiagnosticCode, replaced.Window.Source));
            }
        }
        return [.. current];
    }

    private static WindowObservation Select(IEnumerable<WindowObservation> cluster) => cluster
        .OrderByDescending(w => w.MeasuredAt).ThenByDescending(w => w.ObservedAt)
        .ThenByDescending(w => w.UsedPercent).ThenBy(w => w.Source, StringComparer.Ordinal)
        .ThenBy(w => w.ResetsAt).ThenBy(w => w.Type, StringComparer.Ordinal).First();
}

public static class BlockageEvaluator
{
    public static Blockage Evaluate(string provider, IEnumerable<Observation> observations, Blockage? previous = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var state = previous ?? Blockage.Unknown;
        var cutoff = state.Since;
        foreach (var batch in observations.GroupBy(o => o.EvidenceAt).OrderBy(g => g.Key))
        {
            if (cutoff is { } savedAt && batch.Key <= savedAt)
            {
                continue;
            }
            var blocked = batch.Where(IsBlocked).OrderBy(o => o.Source, StringComparer.Ordinal).FirstOrDefault();
            if (blocked is not null)
            {
                state = new("bloque", batch.Key, blocked.Source);
                continue;
            }
            var positive = batch.Where(o => IsPositive(provider, o))
                .OrderBy(o => o.Source, StringComparer.Ordinal).FirstOrDefault();
            if (positive is not null && (state.Since is null || batch.Key > state.Since))
            {
                state = new("non_bloque", batch.Key, positive.Source);
            }
        }
        return state;
    }

    private static bool IsBlocked(Observation observation) => observation.ReachedType is not null
        || observation.SpendControlReached == true || observation.OrdinaryUsageAllowed == false
        || observation.Windows.Any(w => w.UsedPercent >= 100);

    private static bool IsPositive(string provider, Observation observation) => provider == "claude"
        ? observation.MeasuredAt is not null && observation.Windows.Length > 0
            && observation.Windows.All(w => w.UsedPercent is >= 0 and < 100)
        : observation.OrdinaryUsageAllowed == true
            || (observation.ReachedTypePresent && observation.ReachedType is null && observation.SuccessfulTurn);
}

public static class StateBuilder
{
    public static QuotaState Build(IEnumerable<Observation> observations, IEnumerable<SavedBlockage>? saved = null,
        ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var items = observations.ToArray();
        var savedStates = saved?.ToArray() ?? [];
        var claude = BuildProvider("claude", null, null, items.Where(o => o.Provider == "claude").ToArray(), savedStates, diagnostics);
        var codexItems = items.Where(o => o.Provider == "codex").ToArray();
        var latest = codexItems.OrderByDescending(o => o.Source == "app-server" && o.Account is not null)
            .ThenByDescending(o => o.EvidenceAt).ThenByDescending(o => o.ObservedAt)
            .ThenBy(o => o.Source, StringComparer.Ordinal).FirstOrDefault();
        var account = latest?.Account;
        var codex = BuildProvider("codex", account, null,
            account is null ? [] : codexItems.Where(o => o.Account == account).ToArray(), savedStates, diagnostics);
        var otherKeys = codexItems.Select(o => (o.Account, UnknownSource: o.Account is null ? o.Source : null))
            .Concat(savedStates.Where(s => s.Provider == "codex").Select(s => (s.Account, s.UnknownSource)))
            .Where(key => (key.Account is not null || key.UnknownSource is not null)
                && (key.Account != account || key.UnknownSource is not null))
            .Distinct().OrderBy(key => key.Account, StringComparer.Ordinal)
            .ThenBy(key => key.UnknownSource, StringComparer.Ordinal);
        var others = otherKeys.Select(key => BuildProvider("codex", key.Account, key.UnknownSource,
            codexItems.Where(o => o.Account == key.Account && (key.Account is not null || o.Source == key.UnknownSource)).ToArray(),
            savedStates, diagnostics)).ToArray();
        return new(claude, codex, others);
    }

    public static SavedBlockage[] SaveBlockages(QuotaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new[] { state.Claude, state.Codex }.Concat(state.OtherAccounts)
            .Select(p => new SavedBlockage(p.Provider, p.Account, p.UnknownSource, p.Blockage)).ToArray();
    }

    private static ProviderState BuildProvider(string provider, string? account, string? unknownSource,
        Observation[] observations, SavedBlockage[] saved, ICollection<Diagnostic>? diagnostics)
    {
        var latest = observations.OrderByDescending(o => o.EvidenceAt).ThenByDescending(o => o.ObservedAt)
            .ThenBy(o => o.Source, StringComparer.Ordinal).FirstOrDefault();
        var previous = saved.FirstOrDefault(s => s.Provider == provider && s.Account == account
            && s.UnknownSource == unknownSource)?.Blockage;
        var active = observations.Where(o => o.Source == "app-server").OrderByDescending(o => o.EvidenceAt).FirstOrDefault();
        var clusters = WindowAggregator.CurrentClusters(observations.Where(o => !o.IsHistorical).SelectMany(o => o.Windows), diagnostics);
        return new(provider, account, unknownSource,
            clusters.Select(c => c.Window).ToArray(),
            BlockageEvaluator.Evaluate(provider, observations, previous), latest?.Plan, active?.Credits ?? latest?.Credits,
            active?.OrdinaryUsageAllowed ?? latest?.OrdinaryUsageAllowed, active?.ResetCredits ?? latest?.ResetCredits,
            active?.MeasuredAt, clusters);
    }
}
