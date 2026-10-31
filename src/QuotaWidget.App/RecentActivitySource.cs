using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed record RecentSessionReference(string Provider, string Id, DateTimeOffset ObservedAt,
    [property: JsonIgnore] string? LogPath = null);

public sealed record RecentActivitySnapshot(DateTimeOffset SampledAt, int? Sessions,
    bool IsPartial, string Detail, int? ClaudeSessions = null,
    RecentSessionReference[]? References = null);

public sealed partial class RecentActivitySource
{
    private const int MaximumFiles = 128;
    private const int HeadBytes = 4 * 1024;
    private const int TailBytes = 64 * 1024;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly string[] roots;
    private readonly TimeProvider clock;
    private readonly bool includeClaudeSnapshot;
    private readonly Dictionary<string, CachedFile> cache = new(StringComparer.OrdinalIgnoreCase);

    public RecentActivitySource(IEnumerable<string>? profileDirectories = null, TimeProvider? clock = null)
    {
        includeClaudeSnapshot = profileDirectories is null;
        roots = (profileDirectories ?? DefaultPaths.CodexProfiles).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.clock = clock ?? TimeProvider.System;
    }

    public RecentActivitySnapshot Sample()
    {
        var now = clock.GetUtcNow();
        var partial = false;
        var candidates = new List<FileInfo>();
        foreach (var root in roots)
        {
            foreach (var day in new[] { now.UtcDateTime.Date, now.UtcDateTime.Date.AddDays(-1) })
            {
                var directory = Path.Combine(root, day.ToString("yyyy", CultureInfo.InvariantCulture),
                    day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
                try
                {
                    if (Directory.Exists(directory))
                    { candidates.AddRange(new DirectoryInfo(directory).EnumerateFiles("rollout-*.jsonl", SearchOption.TopDirectoryOnly)); }
                }
                catch (Exception exception) when (IsFileFailure(exception)) { partial = true; }
            }
        }
        var ranked = new List<(FileInfo File, DateTime Modified)>();
        foreach (var file in candidates)
        {
            try { file.Refresh(); ranked.Add((file, file.LastWriteTimeUtc)); }
            catch (Exception exception) when (IsFileFailure(exception)) { partial = true; }
        }
        candidates = ranked.OrderByDescending(item => item.Modified).Select(item => item.File).ToList();
        if (candidates.Count > MaximumFiles) { partial = true; candidates.RemoveRange(MaximumFiles, candidates.Count - MaximumFiles); }
        var selected = candidates.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in cache.Keys.Where(path => !selected.Contains(path)).ToArray()) { cache.Remove(stale); }

        var sessions = new Dictionary<string, RecentSessionReference>(StringComparer.Ordinal);
        var threshold = now - Window;
        foreach (var file in candidates)
        {
            try
            {
                var parsed = Cached(file);
                partial |= parsed.IsPartial;
                if (parsed.SessionId is null) { partial = true; continue; }
                if (parsed.IsSubagent) { continue; }
                if (parsed.LastActivity is null) { partial = true; continue; }
                if (parsed.LastActivity < threshold || parsed.LastActivity > now + TimeSpan.FromMinutes(1)) { continue; }
                var reference = new RecentSessionReference("codex", parsed.SessionId, parsed.LastActivity.Value, file.FullName);
                if (!sessions.TryGetValue(parsed.SessionId, out var previous) || reference.ObservedAt > previous.ObservedAt)
                {
                    sessions[parsed.SessionId] = reference;
                }
            }
            catch (Exception exception) when (IsFileFailure(exception)) { partial = true; }
        }

        int? sessionCount = sessions.Count == 0 ? null : sessions.Count;
        int? claudeSessions = null;
        RecentSessionReference[] claudeReferences = [];
        if (includeClaudeSnapshot)
        {
            try
            {
                var snapshotPath = Path.Combine(DefaultPaths.DataDirectory, "snapshot.json");
                var snapshotFile = new FileInfo(snapshotPath);
                if (!snapshotFile.Exists || snapshotFile.Length > 4 * 1024 * 1024)
                { partial = true; }
                else
                {
                    List<Diagnostic> diagnostics = [];
                    var snapshot = SnapshotStore.Read(snapshotPath, diagnostics);
                    if (snapshot is null || diagnostics.Count > 0) { partial = true; }
                    else
                    {
                        claudeSessions = CountClaudeSessions(snapshot.Observations, now);
                        claudeReferences = ClaudeReferences(snapshot.Observations, now, DefaultPaths.ClaudeDirectory);
                    }
                }
            }
            catch (Exception exception) when (IsFileFailure(exception)) { partial = true; }
        }
        var detail = "Observés sur 15 min · en cours maintenant non exposé";
        if (partial) { detail += " · couverture partielle"; }
        if (sessionCount is null && claudeSessions is null) { detail += " · aucune activité récente confirmée"; }
        var references = sessions.Values.Concat(claudeReferences)
            .OrderByDescending(reference => reference.ObservedAt)
            .ThenBy(reference => reference.Provider, StringComparer.Ordinal)
            .ThenBy(reference => reference.Id, StringComparer.Ordinal)
            .Take(MaximumFiles)
            .ToArray();
        return new(now, sessionCount, partial, detail, claudeSessions, references);
    }

    public static int? CountClaudeSessions(IEnumerable<Observation> observations, DateTimeOffset now)
    {
        var count = ClaudeReferences(observations, now).Length;
        return count == 0 ? null : count;
    }

    public static RecentSessionReference[] ClaudeReferences(
        IEnumerable<Observation> observations,
        DateTimeOffset now,
        string? allowedDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var threshold = now - Window;
        var sessions = new Dictionary<Guid, RecentSessionReference>();
        foreach (var observation in observations)
        {
            if (observation.Provider != "claude" || observation.MeasuredAt is not { } measured
                || measured < threshold || measured > now + TimeSpan.FromMinutes(1)) { continue; }
            var name = Path.GetFileNameWithoutExtension(observation.Source);
            if (!Guid.TryParseExact(name, "D", out var session)) { continue; }
            var reference = new RecentSessionReference("claude", session.ToString("D", CultureInfo.InvariantCulture),
                measured, SafeContainedPath(observation.Source, allowedDirectory));
            if (!sessions.TryGetValue(session, out var previous) || reference.ObservedAt > previous.ObservedAt)
            {
                sessions[session] = reference;
            }
        }
        return sessions.Values.OrderByDescending(reference => reference.ObservedAt)
            .ThenBy(reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? SafeContainedPath(string source, string? allowedDirectory)
    {
        if (allowedDirectory is null) { return null; }
        try
        {
            var root = Path.GetFullPath(allowedDirectory);
            var candidate = Path.GetFullPath(source);
            var relative = Path.GetRelativePath(root, candidate);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                    ? candidate
                    : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private ParsedFile Cached(FileInfo file)
    {
        file.Refresh();
        if (cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length
            && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
        { return cached.Parsed; }
        var parsed = Parse(file.FullName, file.Length);
        cache[file.FullName] = new(file.Length, file.LastWriteTimeUtc, parsed);
        return parsed;
    }

    private static ParsedFile Parse(string path, long reportedLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        var length = Math.Min(reportedLength, stream.Length);
        if (length <= 0) { return new(null, false, null, true); }
        var headLength = checked((int)Math.Min(length, HeadBytes));
        var head = ArrayPool<byte>.Shared.Rent(headLength);
        try
        {
            var read = ReadFully(stream, head, headLength);
            var header = Encoding.UTF8.GetString(head, 0, read);
            var newline = header.IndexOf('\n');
            var firstLine = newline >= 0 ? header[..newline].TrimEnd('\r') : header;
            var metadata = ParseMetadata(firstLine);
            if (metadata.IsSubagent)
            {
                return new(metadata.Id, true, null, false);
            }

            var tailStart = Math.Max(0, length - TailBytes);
            stream.Position = tailStart;
            var tailLength = checked((int)(length - tailStart));
            var tail = ArrayPool<byte>.Shared.Rent(tailLength);
            try
            {
                var tailRead = ReadFully(stream, tail, tailLength);
                var text = Encoding.UTF8.GetString(tail, 0, tailRead);
                if (tailStart > 0)
                {
                    var boundary = text.IndexOf('\n');
                    text = boundary >= 0 ? text[(boundary + 1)..] : "";
                }
                DateTimeOffset? lastActivity = null;
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var timestamp = ActivityTimestamp(line.TrimEnd('\r'));
                    if (timestamp is not null && (lastActivity is null || timestamp > lastActivity)) { lastActivity = timestamp; }
                }
                return new(metadata.Id, metadata.IsSubagent, lastActivity, tailStart > 0 || metadata.Id is null);
            }
            finally { ArrayPool<byte>.Shared.Return(tail); }
        }
        finally { ArrayPool<byte>.Shared.Return(head); }
    }

    private static (string? Id, bool IsSubagent) ParseMetadata(string line)
    {
        if (!SessionMetaPattern().IsMatch(line)) { return (null, false); }
        var id = SessionIdPattern().Match(line).Groups["id"].Value;
        if (id.Length is < 8 or > 128) { return (null, false); }
        var subagent = ParentThreadPattern().IsMatch(line) || SubagentSourcePattern().IsMatch(line);
        if (subagent) { return (id, true); }
        var rootSource = RootSourcePattern().Match(line).Groups["source"].Value.ToLowerInvariant();
        return rootSource is "vscode" or "exec" or "cli" ? (id, false) : (null, false);
    }

    private static DateTimeOffset? ActivityTimestamp(string line)
    {
        if (line.Length is 0 or > 1024 * 1024) { return null; }
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var outer)
                || outer.ValueKind != JsonValueKind.String || outer.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String) { return null; }
            var type = payloadType.GetString();
            if (type is not ("task_started" or "task_complete" or "turn_aborted" or "token_count")) { return null; }
            if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String) { return null; }
            return DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        }
        catch (JsonException) { return null; }
    }

    private static int ReadFully(FileStream stream, byte[] buffer, int requested)
    {
        var total = 0;
        while (total < requested)
        {
            var read = stream.Read(buffer, total, requested - total);
            if (read == 0) { break; }
            total += read;
        }
        return total;
    }

    private static bool IsFileFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.Security.SecurityException;

    [GeneratedRegex("\\\"type\\\"\\s*:\\s*\\\"session_meta\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SessionMetaPattern();

    [GeneratedRegex("\\\"payload\\\"\\s*:\\s*\\{.{0,1536}?\\\"(?:id|session_id)\\\"\\s*:\\s*\\\"(?<id>[A-Za-z0-9_-]+)\\\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("\\\"parent_thread_id\\\"\\s*:\\s*\\\"[A-Za-z0-9_-]+\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex ParentThreadPattern();

    [GeneratedRegex("\\\"source\\\"\\s*:\\s*\\{.{0,256}?\\\"subagent\\\"\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex SubagentSourcePattern();

    [GeneratedRegex("\\\"source\\\"\\s*:\\s*\\\"(?<source>vscode|exec|cli)\\\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RootSourcePattern();

    private sealed record ParsedFile(string? SessionId, bool IsSubagent, DateTimeOffset? LastActivity, bool IsPartial);
    private sealed record CachedFile(long Length, DateTime LastWriteTimeUtc, ParsedFile Parsed);
}
