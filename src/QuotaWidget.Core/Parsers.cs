using System.Text.Json;

namespace QuotaWidget.Core;

public static class ClaudeParser
{
    public static Observation Parse(string json, string source, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected an object.");
        }
        var rendered = root.Property("rendered_at").Timestamp() ?? observedAt;
        var measured = root.Property("measured_at").Timestamp();
        var limits = root.Property("rate_limits");
        List<WindowObservation> windows = [];
        foreach (var (type, duration) in new[] { ("five_hour", 300L), ("seven_day", 10080L) })
        {
            var window = limits.Property(type);
            if (window.ValueKind == JsonValueKind.Object)
            {
                windows.Add(new("claude", "claude", type, duration,
                    window.Property("used_percentage").Number(), window.Property("resets_at").Timestamp(),
                    measured, rendered, source));
            }
        }
        return new("claude", source, rendered, measured, [.. windows], Counter: "claude");
    }
}

public static class CodexParser
{
    public static (string? Account, string? Originator) ParseSessionMetadata(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.Property("type").Text() != "session_meta")
        {
            return (null, null);
        }
        var payload = root.Property("payload");
        return (JsonSupport.Fingerprint(payload.Property("creator_account_id").Text()),
            payload.Property("originator").Text());
    }

    public static Observation? ParseJournal(string line, string source, DateTimeOffset observedAt,
        string? account = null, string? originator = null)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var payload = root.Property("payload");
        var limits = payload.Property("rate_limits");
        if (root.Property("type").Text() != "event_msg" || payload.Property("type").Text() != "token_count"
            || limits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var measured = root.Property("timestamp").Timestamp();
        var reached = limits.Property("rate_limit_reached_type");
        var succeeded = reached.ValueKind == JsonValueKind.Null
            && payload.Property("info").Property("last_token_usage").Property("total_tokens").Number() > 0;
        return ParseLimit(limits, false, source, observedAt, measured, account, originator,
            limits.Property("limit_id").Text(), null, null) with { SuccessfulTurn = succeeded };
    }

    // Inputs are app-server response payloads, not JSON-RPC envelopes.
    public static Observation[] ParseAppServer(string json, string source, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.Property("rateLimits").ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Missing rateLimits object.");
        }
        var account = JsonSupport.Fingerprint(root.Property("accountId").Text());
        var allowed = root.Property("ordinaryUsageAllowed").Boolean();
        var resetCredits = ParseResetCredits(root.Property("rateLimitResetCredits"));
        var buckets = root.Property("rateLimitsByLimitId");
        List<Observation> observations = [];
        if (buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    observations.Add(ParseLimit(bucket.Value, true, source, observedAt, observedAt,
                        account, "app-server", bucket.Name, allowed, resetCredits));
                }
            }
        }
        var legacy = root.Property("rateLimits");
        var legacyId = legacy.Property("limitId").Text();
        if (observations.Count == 0 || (legacyId is not null && observations.TrueForAll(x => x.Counter != legacyId)))
        {
            observations.Add(ParseLimit(legacy, true, source, observedAt, observedAt, account,
                "app-server", legacyId, allowed, resetCredits));
        }
        return [.. observations];
    }

    private static Observation ParseLimit(JsonElement limits, bool camelCase, string source,
        DateTimeOffset observedAt, DateTimeOffset? measuredAt, string? account, string? originator,
        string? counter, bool? allowed, ResetCredits? resetCredits)
    {
        List<WindowObservation> windows = [];
        foreach (var type in new[] { "primary", "secondary" })
        {
            var window = limits.Property(type);
            if (window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var used = window.Property(camelCase ? "usedPercent" : "used_percent");
            double? percent = camelCase ? used.Integer() : used.Number();
            var duration = window.Property(camelCase ? "windowDurationMins" : "window_minutes").Integer();
            windows.Add(new("codex", counter, type, duration > 0 ? duration : null, percent,
                window.Property(camelCase ? "resetsAt" : "resets_at").Timestamp(), measuredAt, observedAt, source));
        }
        var reached = limits.Property(camelCase ? "rateLimitReachedType" : "rate_limit_reached_type");
        return new("codex", source, observedAt, measuredAt, [.. windows], account, originator, counter,
            limits.Property(camelCase ? "planType" : "plan_type").Text(),
            JsonSupport.ParseCredits(limits.Property("credits"), camelCase), allowed, resetCredits,
            reached.ValueKind != JsonValueKind.Undefined, reached.Text(),
            limits.Property(camelCase ? "spendControlReached" : "spend_control_reached").Boolean());
    }

    private static ResetCredits? ParseResetCredits(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var rows = value.Property("credits");
        ResetCredit[]? credits = rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(row => new ResetCredit(row.Property("id").Text(),
                row.Property("resetType").Text(), row.Property("status").Text(),
                row.Property("grantedAt").Timestamp(), row.Property("expiresAt").Timestamp(),
                row.Property("title").Text(), row.Property("description").Text())).ToArray() : null;
        return new(value.Property("availableCount").Integer(), credits);
    }
}
