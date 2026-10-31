using System.Text.Json;
using System.Text;

namespace QuotaWidget.Core;

public static class AtomicJsonFile
{
    public static T? Read<T>(string path) where T : class => SharedFiles.Retry(() =>
    {
        using var stream = SharedFiles.Open(path);
        return JsonSerializer.Deserialize<T>(stream, JsonSupport.Options);
    });

    public static void Write<T>(string path, T value)
        => WriteStream(path, stream => JsonSerializer.Serialize(stream, value, JsonSupport.Options));

    public static void WriteText(string path, string value)
        => WriteStream(path, stream => stream.Write(Encoding.UTF8.GetBytes(value)));

    private static void WriteStream(string path, Action<FileStream> write)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("A parent directory is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            // Windows can transiently deny replacement while another process opens the target.
            var deadline = TimeSpan.FromSeconds(2);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var backoffMilliseconds = 25;
            while (true)
            {
                try
                {
                    File.Move(temporary, fullPath, true);
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var remaining = deadline - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) { throw; }
                    var delay = Math.Min(backoffMilliseconds,
                        Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                    Thread.Sleep(delay);
                    backoffMilliseconds = Math.Min(backoffMilliseconds * 2, 250);
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

public static class SnapshotStore
{
    public static Snapshot? Read(string path, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var snapshot = AtomicJsonFile.Read<Snapshot>(path);
            if (snapshot is null || snapshot.Version != 1 || snapshot.Observations is null
                || snapshot.Blockages is null || snapshot.Diagnostics is null
                || snapshot.Observations.Any(o => o is null || o.Windows is null || o.Source is null
                    || !ValidAccount(o.Account) || o.Windows.Any(w => w is null))
                || snapshot.Blockages.Any(b => b is null || b.Blockage is null || !ValidAccount(b.Account)))
            {
                diagnostics.Add(new("instantane_invalide", path));
                return null;
            }
            return snapshot;
        }
        catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
        {
            diagnostics.Add(new("instantane_illisible", path));
            return null;
        }
    }

    public static void Write(string path, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Observations.Any(o => !ValidAccount(o.Account))
            || snapshot.Blockages.Any(b => !ValidAccount(b.Account)))
        {
            throw new ArgumentException("Accounts must be SHA-256 fingerprints of eight lowercase hexadecimal characters.", nameof(snapshot));
        }
        AtomicJsonFile.Write(path, snapshot);
        var state = StateBuilder.Build(snapshot.Observations, snapshot.Blockages);
        AtomicJsonFile.WriteText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "statusline.tsv"),
            StatusLineFormatter.Format(state, snapshot.WrittenAt, TimeZoneInfo.Local));
    }

    internal static bool ValidAccount(string? account) => account is null
        || (account.Length == 8 && account.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
}

public sealed class QuotaCollector(TimeProvider clock, CodexActiveSource? activeSource = null)
{
    public Snapshot Collect(string claudeDirectory, IEnumerable<string> codexProfiles, Snapshot? previous = null)
        => CollectAsync(claudeDirectory, codexProfiles, previous).GetAwaiter().GetResult();

    public async Task<Snapshot> CollectAsync(string claudeDirectory, IEnumerable<string> codexProfiles,
        Snapshot? previous = null, bool resumeAuthentication = false, CancellationToken cancellationToken = default)
    {
        var claude = new ClaudeSource(clock).Read(claudeDirectory);
        var codex = new CodexPassiveSource(clock).Read(codexProfiles);
        var observations = claude.Observations.Concat(codex.Observations).ToList();
        List<Diagnostic> diagnostics = [.. claude.Diagnostics, .. codex.Diagnostics];
        var active = activeSource is null ? null
            : await activeSource.ReadAsync(resumeAuthentication, cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            observations.AddRange(active.Observations);
            if (active.Diagnostic is { } diagnostic) { diagnostics.Add(diagnostic); }
        }
        if (previous is not null)
        {
            var emptyProviders = new HashSet<string>(StringComparer.Ordinal);
            if (claude.Observations.Length == 0)
            {
                emptyProviders.Add("claude");
            }
            if (codex.Observations.Length == 0)
            {
                emptyProviders.Add("codex");
            }
            var unreadable = diagnostics.Where(d => d.Code is "claude_fichier_ignore" or "codex_fichier_ignore")
                .Select(d => d.Source).ToHashSet(StringComparer.Ordinal);
            // Preserve original timestamps: a failed collection is not a new measurement.
            var retained = previous.Observations.Where(o => !o.IsHistorical
                    && !(o.Source == "app-server" && active?.Observations.Length > 0)
                    && (emptyProviders.Contains(o.Provider) || unreadable.Contains(o.Source)
                        || (o.Source == "app-server" && active?.Observations.Length is not > 0)))
                .GroupBy(o => (o.Provider, o.Account, o.Source, o.Counter))
                .Select(g => g.OrderByDescending(o => o.EvidenceAt).ThenByDescending(o => o.ObservedAt).First()).ToArray();
            observations.AddRange(retained);
            diagnostics.AddRange(retained.Select(o => o.Source).Distinct(StringComparer.Ordinal)
                .Select(source => new Diagnostic("derniere_mesure_conservee", source)));
        }
        var state = StateBuilder.Build(observations, previous?.Blockages, diagnostics);
        return new(1, clock.GetUtcNow(), [.. observations], StateBuilder.SaveBlockages(state), [.. diagnostics]);
    }
}
