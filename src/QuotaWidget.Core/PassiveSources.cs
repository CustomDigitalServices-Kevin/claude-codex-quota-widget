using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaWidget.Core;

internal static partial class SharedFiles
{
    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeNamePattern();

    internal static bool IsClaudeName(string path) => ClaudeNamePattern().IsMatch(Path.GetFileName(path));

    internal static bool IsRegular(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    internal static T Retry<T>(Func<T> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (attempt < 2 && IsReadFailure(exception))
            {
                Thread.Sleep(50);
            }
        }
    }

    internal static bool IsReadFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or JsonException;

    internal static string ReadText(string path)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public sealed class ClaudeSource(TimeProvider clock)
{
    public CollectionResult Read(string directory)
    {
        List<Observation> observations = [];
        List<Diagnostic> diagnostics = [];
        try
        {
            if (!Directory.Exists(directory) || !SharedFiles.IsRegular(directory))
            {
                return new([], []);
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (!SharedFiles.IsClaudeName(path))
                {
                    continue;
                }
                try
                {
                    var observation = SharedFiles.Retry(() =>
                    {
                        if (!SharedFiles.IsRegular(path))
                        {
                            return null;
                        }
                        using var stream = SharedFiles.Open(path);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return ClaudeParser.Parse(reader.ReadToEnd(), path, clock.GetUtcNow());
                    });
                    if (observation is not null)
                    {
                        observations.Add(observation);
                    }
                }
                catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
                {
                    diagnostics.Add(new("claude_fichier_ignore", path));
                }
            }
        }
        catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
        {
            diagnostics.Add(new("claude_dossier_illisible", directory));
        }
        return new([.. observations], [.. diagnostics]);
    }

    public CollectionResult Purge(string directory)
    {
        List<Diagnostic> diagnostics = [];
        var now = clock.GetUtcNow();
        try
        {
            if (!Directory.Exists(directory) || !SharedFiles.IsRegular(directory))
            {
                return new([], []);
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (!SharedFiles.IsClaudeName(path))
                {
                    continue;
                }
                try
                {
                    if (!SharedFiles.IsRegular(path))
                    {
                        continue;
                    }
                    DateTimeOffset? weeklyReset = null;
                    try
                    {
                        var observation = SharedFiles.Retry(() =>
                            ClaudeParser.Parse(SharedFiles.ReadText(path), path, now));
                        weeklyReset = observation.Windows.FirstOrDefault(w => w.Type == "seven_day")?.ResetsAt;
                    }
                    catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
                    {
                        diagnostics.Add(new("purge_mesure_illisible", path));
                    }
                    var expired = weeklyReset is { } reset ? reset < now.AddDays(-1)
                        : File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-8);
                    if (expired && SharedFiles.IsRegular(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
                {
                    diagnostics.Add(new("purge_fichier_ignore", path));
                }
            }
        }
        catch (Exception exception) when (SharedFiles.IsReadFailure(exception))
        {
            diagnostics.Add(new("purge_dossier_illisible", directory));
        }
        return new([], [.. diagnostics]);
    }
}

