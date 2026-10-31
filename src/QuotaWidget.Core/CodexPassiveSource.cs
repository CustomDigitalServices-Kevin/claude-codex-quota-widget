using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

public sealed class CodexPassiveSource(TimeProvider clock)
{
    private const int HeadLimit = 512 * 1024;
    private const int InitialTail = 64 * 1024;
    private const int MaximumTail = 1024 * 1024;

    public CollectionResult Read(IEnumerable<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        List<Observation> observations = [];
        List<Diagnostic> diagnostics = [];
        foreach (var profile in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var path in Discover(profile, diagnostics))
            {
                var result = ReadFile(path);
                observations.AddRange(result.Observations);
                diagnostics.AddRange(result.Diagnostics);
            }
        }
        return new([.. observations], [.. diagnostics]);
    }

    private string[] Discover(string profile, List<Diagnostic> diagnostics)
    {
        List<FileInfo> files = [];
        try
        {
            if (!Directory.Exists(profile) || !SharedFiles.IsRegular(profile))
            {
                return [];
            }
            var now = clock.GetUtcNow();
            for (var day = 0; day < 8; day++)
            {
                var date = now.AddDays(-day);
                var year = Path.Combine(profile, date.ToString("yyyy", CultureInfo.InvariantCulture));
                var month = Path.Combine(year, date.ToString("MM", CultureInfo.InvariantCulture));
                var directory = Path.Combine(month, date.ToString("dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(directory) || !SharedFiles.IsRegular(year)
                    || !SharedFiles.IsRegular(month) || !SharedFiles.IsRegular(directory))
                {
                    continue;
                }
                foreach (var path in Directory.EnumerateFiles(directory, "rollout-*.jsonl"))
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        files.Add(file);
                    }
                }
            }
        }
        catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
        {
            diagnostics.Add(new("codex_dossier_illisible", profile));
        }
        return files.OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal).Take(50).Select(file => file.FullName).ToArray();
    }

    public CollectionResult ReadFile(string path)
    {
        List<Diagnostic> diagnostics = [];
        try
        {
            return SharedFiles.Retry(() => ReadFileCore(path, diagnostics));
        }
        catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
        {
            diagnostics.Add(new("codex_fichier_ignore", path));
            return new([], [.. diagnostics]);
        }
    }

    private CollectionResult ReadFileCore(string path, List<Diagnostic> diagnostics)
    {
        if (!SharedFiles.IsRegular(path))
        {
            return new([], []);
        }
        using var stream = SharedFiles.Open(path);
        var length = stream.Length;
        var head = new byte[(int)Math.Min(length, HeadLimit)];
        stream.ReadExactly(head);
        var newline = Array.IndexOf(head, (byte)'\n');
        string? account = null;
        string? originator = null;
        if (newline >= 0 || length < HeadLimit)
        {
            try
            {
                (account, originator) = CodexParser.ParseSessionMetadata(
                    Encoding.UTF8.GetString(head, 0, newline >= 0 ? newline : head.Length).TrimStart('\uFEFF'));
            }
            catch (JsonException)
            {
                diagnostics.Add(new("codex_entete_invalide", path));
            }
        }
        else
        {
            diagnostics.Add(new("codex_entete_trop_longue", path));
        }

        // Expand the same tail; do not duplicate evidence between overlapping reads.
        for (var size = InitialTail; size <= MaximumTail; size *= 2)
        {
            var start = Math.Max(0, length - size);
            stream.Position = start;
            var bytes = new byte[(int)(length - start)];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');
            List<Observation> observations = [];
            var invalid = 0;
            for (var index = start == 0 ? 0 : 1; index < lines.Length - 1; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    continue;
                }
                try
                {
                    var observation = CodexParser.ParseJournal(lines[index].TrimStart('\uFEFF'), path,
                        clock.GetUtcNow(), account, originator);
                    if (observation is not null)
                    {
                        observations.Add(observation);
                    }
                }
                catch (JsonException)
                {
                    invalid++;
                }
            }
            if (observations.Count > 0 || start == 0 || size == MaximumTail)
            {
                if (invalid > 0)
                {
                    diagnostics.Add(new("codex_lignes_invalides", path, invalid));
                }
                if (!string.IsNullOrWhiteSpace(lines[^1]))
                {
                    diagnostics.Add(new("codex_ligne_incomplete", path));
                }
                if (observations.Count == 0)
                {
                    diagnostics.Add(new("codex_mesure_absente_dans_borne", path));
                }
                // Only the last event per counter supplies displayed windows; earlier events remain blockage evidence.
                var latestIndices = observations.Select((observation, index) => (observation.Counter, Index: index))
                    .GroupBy(item => item.Counter).Select(group => group.Last().Index).ToHashSet();
                return new(observations.Select((observation, index) =>
                    observation with { IsHistorical = !latestIndices.Contains(index) }).ToArray(), [.. diagnostics]);
            }
        }
        throw new InvalidOperationException("Tail bounds must include at least one read.");
    }
}
