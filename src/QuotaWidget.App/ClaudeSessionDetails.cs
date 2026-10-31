using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.App;

internal static class ClaudeSessionDetails
{
    private const int MaximumProjectDirectories = 4096;
    internal const string LocalNotice = "Certains détails peuvent manquer dans les données locales.";

    internal static SessionDetails Read(RecentSessionReference reference, string stateRoot,
        string projectsRoot, SessionReadBudget budget, CancellationToken token)
    {
        if (!Guid.TryParseExact(reference.Id, "D", out var session))
        { throw new InvalidDataException("Claude session identifier is invalid."); }
        var id = session.ToString("D", CultureInfo.InvariantCulture);
        if (!SessionMetadataReader.TryResolveRegularFile(reference.LogPath, [stateRoot],
                out var statePath, out _)
            || !string.Equals(Path.GetFileName(statePath), id + ".json", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("Claude state source is invalid."); }

        var state = ReadState(statePath, id, budget, token);
        var runtimePath = FindRuntime(projectsRoot, id, token);
        var runtime = runtimePath is null ? null : ReadRuntime(runtimePath, id, budget, token);
        return new SessionDetails("claude", id, reference.ObservedAt,
            state.Name ?? runtime?.Name, state.Model ?? runtime?.Model,
            state.Effort ?? runtime?.Effort, state.Cwd ?? runtime?.Cwd,
            AgentNickname: state.AgentNickname, ContextAt: runtime?.ObservedAt, Notice: LocalNotice);
    }

    private static ClaudeState ReadState(string path, string id, SessionReadBudget budget,
        CancellationToken token)
    {
        var text = SessionMetadataReader.ReadText(path, SessionMetadataReader.MaximumFileBytes, budget, token);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 20 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return new(); }
            var embeddedId = String(root, "session_id") ?? String(root, "sessionId");
            if (embeddedId is not null && !string.Equals(embeddedId, id, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("Claude state identifier is inconsistent."); }
            return new(Safe(String(root, "session_name")),
                NestedString(root, "model", "id"), NestedString(root, "effort", "level"),
                SafePath(String(root, "cwd")), NestedString(root, "agent", "name"));
        }
        catch (JsonException) { return new(); }
    }

    private static string? FindRuntime(string projectsRoot, string id, CancellationToken token)
    {
        if (!SessionMetadataReader.IsSafeDirectory(projectsRoot)) { return null; }
        var candidates = new List<string>(1);
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(projectsRoot, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumProjectDirectories + 1)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaximumProjectDirectories)
            { throw new InvalidDataException("Claude project directory limit exceeded."); }
            if (!SessionMetadataReader.IsSafeDirectory(directory)) { continue; }
            var candidate = Path.Combine(directory, id + ".jsonl");
            if (SessionMetadataReader.TryResolveRegularFile(candidate, [directory], out var safePath, out _))
            { candidates.Add(safePath); }
            if (candidates.Count > 1) { throw new InvalidDataException("Claude runtime source is ambiguous."); }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ClaudeRuntime ReadRuntime(string path, string id, SessionReadBudget budget,
        CancellationToken token)
    {
        ClaudeRuntime latest = new();
        string? name = null;
        foreach (var line in SessionMetadataReader.ReadTailLines(path,
            SessionMetadataReader.MaximumFileBytes, budget, token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 24 });
                var root = document.RootElement;
                var type = String(root, "type");
                if (type == "ai-title" && string.Equals(String(root, "sessionId"), id,
                    StringComparison.OrdinalIgnoreCase))
                {
                    name = Safe(String(root, "aiTitle"));
                    continue;
                }
                if (type != "assistant" || !Timestamp(root, out var timestamp)) { continue; }
                var embeddedId = String(root, "sessionId") ?? String(root, "session_id");
                if (embeddedId is not null && !string.Equals(embeddedId, id, StringComparison.OrdinalIgnoreCase))
                { continue; }
                if (latest.ObservedAt is not null && timestamp < latest.ObservedAt) { continue; }
                latest = new(timestamp, null, Safe(String(root, "effort")),
                    SafePath(String(root, "cwd")), null);
                if (Object(root, "message", out var message)) { latest = latest with { Model = Safe(String(message, "model")) }; }
            }
            catch (JsonException) { }
        }
        return latest with { Name = name };
    }

    private static string? NestedString(JsonElement root, string objectName, string propertyName) =>
        Object(root, objectName, out var nested) ? Safe(String(nested, propertyName)) : null;

    private static bool Object(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Timestamp(JsonElement root, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(String(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static string? Safe(string? value) => SessionMetadataReader.Sanitize(value, 256);
    private static string? SafePath(string? value) => SessionMetadataReader.Sanitize(value, 1024);

    private sealed record ClaudeState(string? Name = null, string? Model = null,
        string? Effort = null, string? Cwd = null, string? AgentNickname = null);
    private sealed record ClaudeRuntime(DateTimeOffset? ObservedAt = null, string? Model = null,
        string? Effort = null, string? Cwd = null, string? Name = null);
}
