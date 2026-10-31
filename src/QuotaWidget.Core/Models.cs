using System.Text.Json.Serialization;

namespace QuotaWidget.Core;

public sealed record WindowObservation(
    string Provider, string? Counter, string Type, long? DurationMinutes,
    double? UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? MeasuredAt,
    DateTimeOffset ObservedAt, string Source);

public sealed record Credits(double? Balance, bool? Unlimited, bool? HasCredits);

public sealed record ResetCredit(string? Id, string? ResetType, string? Status,
    DateTimeOffset? GrantedAt, DateTimeOffset? ExpiresAt, string? Title, string? Description);

public sealed record ResetCredits(long? AvailableCount, ResetCredit[]? Credits);

public sealed record Observation(
    string Provider, string Source, DateTimeOffset ObservedAt, DateTimeOffset? MeasuredAt,
    WindowObservation[] Windows,
    string? Account = null, string? Originator = null, string? Counter = null,
    string? Plan = null, Credits? Credits = null, bool? OrdinaryUsageAllowed = null,
    ResetCredits? ResetCredits = null, bool ReachedTypePresent = false,
    string? ReachedType = null, bool? SpendControlReached = null, bool SuccessfulTurn = false,
    bool IsHistorical = false)
{
    [JsonIgnore]
    public DateTimeOffset EvidenceAt => MeasuredAt ?? ObservedAt;
}

public sealed record Diagnostic(string Code, string Source, int Count = 1,
    DateTimeOffset? NextAttemptAt = null, DateTimeOffset? LastAttemptAt = null);

public sealed record Blockage(string State, DateTimeOffset? Since, string? Provenance)
{
    public static Blockage Unknown { get; } = new("inconnu", null, null);
}

public sealed record SavedBlockage(string Provider, string? Account, string? UnknownSource, Blockage Blockage);

public sealed record Snapshot(int Version, DateTimeOffset WrittenAt, Observation[] Observations,
    SavedBlockage[] Blockages, Diagnostic[] Diagnostics);

public sealed record CollectionResult(Observation[] Observations, Diagnostic[] Diagnostics);

public sealed record ProviderState(string Provider, string? Account, string? UnknownSource,
    WindowObservation[] Windows, Blockage Blockage, string? Plan, Credits? Credits,
    bool? OrdinaryUsageAllowed, ResetCredits? ResetCredits, DateTimeOffset? ActiveMeasuredAt = null,
    WindowCluster[]? WindowClusters = null);

public sealed record WindowCluster(WindowObservation Window, DateTimeOffset? ResetAnchor);

public sealed record QuotaState(ProviderState Claude, ProviderState Codex, ProviderState[] OtherAccounts);

public sealed record WindowMetrics(double? ElapsedFraction, double? PaceDifference, string? Color,
    double? AgeSeconds, bool ResetPassed);

public sealed record ArbitrationHistory(string Verdict,
    [property: JsonPropertyName("etape")] int Step,
    [property: JsonPropertyName("date")] DateTimeOffset At);

public sealed record ArbitrationNumbers(bool Eligible, double Used, double? RemainingFraction,
    double Reserve, double? UsedFiveHour, double? AgeSeconds);

public sealed record ArbitrationResult(string Verdict, bool Uncertain, int Step, string Reason,
    double RelativeDifference, ArbitrationNumbers Claude, ArbitrationNumbers Codex);
