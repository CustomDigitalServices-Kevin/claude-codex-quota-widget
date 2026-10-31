using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.App;

internal static class CodexSessionDetails
{
    private const int HeadBytes = 256 * 1024;
    private const int TailBytes = SessionMetadataReader.MaximumFileBytes - HeadBytes;
    private const int IndexBytes = 256 * 1024;
    private const int IndexHeadBytes = 4 * 1024;

    internal static SessionDetails Read(RecentSessionReference reference, string[] roots,
        SessionReadBudget budget, CancellationToken token)
    {
        if (!IsValidId(reference.Id)
            || !SessionMetadataReader.TryResolveRegularFile(reference.LogPath, roots, out var path, out var sessionsRoot)
            || !IsRolloutName(path))
        { throw new InvalidDataException("Codex session source is invalid."); }

        var lines = SessionMetadataReader.ReadHeadAndTailLines(path, HeadBytes, TailBytes, budget, token);
        if (lines.Head.Length == 0) { throw new InvalidDataException("Codex session metadata is absent."); }
        var metadata = ParseMetadata(lines.Head[0], reference.Id);
        CodexContext? latest = null;
        foreach (var line in lines.All().Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var context = ParseContext(line);
            if (context is not null && (latest is null || context.ObservedAt >= latest.ObservedAt)) { latest = context; }
        }

        return new SessionDetails("codex", reference.Id, reference.ObservedAt,
            ReadName(sessionsRoot, reference.Id, budget, token), latest?.Model, latest?.Effort,
            latest?.Cwd ?? metadata.Cwd, metadata.Originator, metadata.AgentRole,
            metadata.AgentNickname, latest?.ObservedAt, CreatedAt: metadata.CreatedAt);
    }

    private static CodexMetadata ParseMetadata(string line, string expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 20 });
            var root = document.RootElement;
            if (!IsType(root, "session_meta") || !Object(root, "payload", out var payload))
            { throw new InvalidDataException("Codex session metadata is malformed."); }
            var id = String(payload, "id") ?? String(payload, "session_id");
            if (!string.Equals(id, expectedId, StringComparison.Ordinal))
            { throw new InvalidDataException("Codex session identifier is inconsistent."); }

            var originator = String(payload, "originator");
            var role = String(payload, "agent_role");
            var nickname = String(payload, "agent_nickname");
            if (Object(payload, "source", out var source) && Object(source, "subagent", out var subagent))
            {
                if (subagent.TryGetProperty("thread_spawn", out _))
                { throw new InvalidDataException("Codex subagent details are not eligible."); }
            }
            return new(SafePath(String(payload, "cwd")), Safe(originator),
                Safe(role), Safe(nickname), CreationTimestamp(payload));
        }
        catch (JsonException exception) { throw new InvalidDataException("Codex session metadata is malformed.", exception); }
    }

    private static CodexContext? ParseContext(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 20 });
            var root = document.RootElement;
            if (!IsType(root, "turn_context") || !Timestamp(root, out var timestamp)
                || !Object(root, "payload", out var payload)) { return null; }
            return new(timestamp, Safe(String(payload, "model")), Safe(String(payload, "effort")),
                SafePath(String(payload, "cwd")));
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadName(string sessionsRoot, string id, SessionReadBudget budget,
        CancellationToken token)
    {
        var parent = Directory.GetParent(sessionsRoot)?.FullName;
        if (parent is null) { return null; }
        var indexPath = Path.Combine(parent, "session_index.jsonl");
        if (!SessionMetadataReader.TryResolveRegularFile(indexPath, [parent], out var safeIndex, out _)) { return null; }
        SessionLineSegments lines;
        try
        {
            lines = SessionMetadataReader.ReadHeadAndTailLines(safeIndex, IndexHeadBytes,
                IndexBytes - IndexHeadBytes, budget, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (SessionMetadataReader.IsFileFailure(exception)) { return null; }
        string? name = null;
        foreach (var line in lines.All())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (string.Equals(String(root, "id"), id, StringComparison.Ordinal))
                { name = Safe(String(root, "thread_name")); }
            }
            catch (JsonException) { }
        }
        return name;
    }

    private static bool IsRolloutName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidId(string value) => value.Length is >= 8 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsType(JsonElement element, string expected) =>
        element.ValueKind == JsonValueKind.Object
        && string.Equals(String(element, "type"), expected, StringComparison.Ordinal);

    private static bool Object(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Timestamp(JsonElement root, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(String(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static DateTimeOffset? CreationTimestamp(JsonElement payload)
    {
        if (!Timestamp(payload, out var value) || value > DateTimeOffset.UtcNow) { return null; }
        return value;
    }

    private static string? Safe(string? value) => SessionMetadataReader.Sanitize(value, 256);
    private static string? SafePath(string? value) => SessionMetadataReader.Sanitize(value, 1024);

    private sealed record CodexMetadata(string? Cwd, string? Originator, string? AgentRole,
        string? AgentNickname, DateTimeOffset? CreatedAt);
    private sealed record CodexContext(DateTimeOffset ObservedAt, string? Model, string? Effort, string? Cwd);
}
