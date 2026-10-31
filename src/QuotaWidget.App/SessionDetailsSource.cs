using QuotaWidget.Core;
using System.Text.Json;

namespace QuotaWidget.App;

public sealed record SessionDetails(
    string Provider,
    string Id,
    DateTimeOffset ObservedAt,
    string? Name = null,
    string? Model = null,
    string? Effort = null,
    string? ProjectPath = null,
    string? Originator = null,
    string? AgentRole = null,
    string? AgentNickname = null,
    DateTimeOffset? ContextAt = null,
    string? Notice = null,
    DateTimeOffset? CreatedAt = null);

public sealed class SessionDetailsSource
{
    private const int MaximumSessions = 32;
    private const int MaximumReadBytes = MaximumSessions * SessionMetadataReader.MaximumFileBytes;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);
    private readonly string[] codexRoots;
    private readonly string claudeStateRoot;
    private readonly string claudeProjectsRoot;

    public SessionDetailsSource(string[]? codexProfiles = null, string? claudeStateDirectory = null,
        string? claudeProjectsDirectory = null)
    {
        codexRoots = Normalize(codexProfiles ?? DefaultPaths.CodexProfiles);
        claudeStateRoot = FullPathOrEmpty(claudeStateDirectory ?? DefaultPaths.ClaudeDirectory);
        claudeProjectsRoot = FullPathOrEmpty(claudeProjectsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));
    }

    public SessionDetails[] Read(IEnumerable<RecentSessionReference> refs, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(refs);
        var now = DateTimeOffset.UtcNow;
        var earliest = now - RecentWindow;
        var result = new List<SessionDetails>(MaximumSessions);
        var budget = new SessionReadBudget(MaximumReadBytes);
        foreach (var reference in refs)
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == MaximumSessions) { break; }
            if (reference.ObservedAt < earliest || reference.ObservedAt > now + TimeSpan.FromMinutes(1)) { continue; }
            result.Add(ReadOne(reference, budget, token));
        }
        return [.. result];
    }

    private SessionDetails ReadOne(RecentSessionReference reference, SessionReadBudget budget,
        CancellationToken token)
    {
        try
        {
            return reference.Provider switch
            {
                "codex" => CodexSessionDetails.Read(reference, codexRoots, budget, token),
                "claude" => ClaudeSessionDetails.Read(reference, claudeStateRoot, claudeProjectsRoot, budget, token),
                _ => Failure(reference, "unknown", "Fournisseur de session non pris en charge."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsIsolatedFailure(exception))
        {
            return Failure(reference, reference.Provider == "claude" ? "claude" : "codex",
                reference.Provider == "claude" ? "Détails Claude indisponibles." : "Détails Codex indisponibles.");
        }
    }

    private static SessionDetails Failure(RecentSessionReference reference, string provider, string notice) =>
        new(provider, SessionMetadataReader.Sanitize(reference.Id, 128) ?? "inconnu",
            reference.ObservedAt, Notice: notice);

    private static bool IsIsolatedFailure(Exception exception) =>
        SessionMetadataReader.IsFileFailure(exception) || exception is JsonException
        or ArgumentException or NotSupportedException or PathTooLongException
        or InvalidOperationException;

    private static string[] Normalize(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(FullPathOrEmpty)
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string FullPathOrEmpty(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException) { return string.Empty; }
    }
}
