using System.Text;
using System.Globalization;
using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class RecentActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CountsRecentRootSessionsDeduplicatesIdsAndExcludesSubagents()
    {
        using var directory = new TestDirectory();
        Write(directory.Root, "a", Meta("root-0001", false), Event("task_started", Now.AddMinutes(-2)));
        Write(directory.Root, "b", Meta("agent-001", true), Event("token_count", Now.AddMinutes(-1)));
        Write(directory.Root, "c", Meta("root-0001", false), Event("task_complete", Now.AddMinutes(-3)));
        Write(directory.Root, "stale", Meta("old-00001", false), Event("task_complete", Now.AddMinutes(-16)));
        Write(directory.Root, "future", Meta("future-01", false), Event("task_started", Now.AddMinutes(2)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Equal(1, result.Sessions);
        Assert.False(result.IsPartial);
        Assert.Contains("en cours maintenant non exposé", result.Detail, StringComparison.Ordinal);
        var reference = Assert.Single(Assert.IsType<RecentSessionReference[]>(result.References));
        Assert.Equal("codex", reference.Provider);
        Assert.Equal("root-0001", reference.Id);
        Assert.Equal(Now.AddMinutes(-2), reference.ObservedAt);
        Assert.Equal(RolloutPath(directory.Root, "a"), reference.LogPath);
    }

    [Fact]
    public void DuplicateRootAcrossProfilesKeepsLatestTimestampAndPath()
    {
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        Write(first.Root, "older", Meta("shared-001", false), Event("task_started", Now.AddMinutes(-8)));
        Write(second.Root, "latest", Meta("shared-001", false), Event("task_complete", Now.AddMinutes(-1)));

        var result = new RecentActivitySource([first.Root, second.Root], new TestClock(Now)).Sample();

        Assert.Equal(1, result.Sessions);
        var reference = Assert.Single(Assert.IsType<RecentSessionReference[]>(result.References));
        Assert.Equal(Now.AddMinutes(-1), reference.ObservedAt);
        Assert.Equal(RolloutPath(second.Root, "latest"), reference.LogPath);
    }

    [Fact]
    public void ReturnsUnknownInsteadOfZeroWhenNothingRecentIsConfirmed()
    {
        using var directory = new TestDirectory();
        Write(directory.Root, "old", Meta("old-00002", false), Event("task_complete", Now.AddHours(-1)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
        Assert.Contains("aucune activité récente confirmée", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksCoveragePartialForTruncatedTailAndKeepsConfirmedCount()
    {
        using var directory = new TestDirectory();
        var padding = new string('x', 70 * 1024);
        Write(directory.Root, "large", Meta("root-0003", false), "{\"padding\":\"" + padding + "\"}",
            Event("task_started", Now.AddMinutes(-1)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Equal(1, result.Sessions);
        Assert.True(result.IsPartial);
        Assert.Contains("couverture partielle", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsFileScanAndReportsPartialCoverage()
    {
        using var directory = new TestDirectory();
        for (var index = 0; index < 130; index++)
        {
            Write(directory.Root, index.ToString("D3", CultureInfo.InvariantCulture),
                Meta("root-" + index.ToString("D4", CultureInfo.InvariantCulture), false),
                Event("token_count", Now.AddMinutes(-1)));
        }

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Equal(128, result.Sessions);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void IgnoresConversationContentAndUnknownEvents()
    {
        using var directory = new TestDirectory();
        Write(directory.Root, "content", Meta("root-0004", false),
            "{\"timestamp\":\"" + Now.AddMinutes(-1).ToString("O") + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"task_started\"}}");

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
    }

    [Fact]
    public void MissingMetadataAndInvalidJsonProducePartialUnknownResult()
    {
        using var directory = new TestDirectory();
        Write(directory.Root, "broken", "not json", Event("task_started", Now.AddMinutes(-1)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void MetadataWithoutExplicitKnownSourceIsIgnored()
    {
        using var directory = new TestDirectory();
        var metadata = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"root-unknown\"}}";
        Write(directory.Root, "unknown", metadata, Event("token_count", Now.AddMinutes(-1)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void MalformedJsonTypesDoNotEscapeTheBoundedScanner()
    {
        using var directory = new TestDirectory();
        Write(directory.Root, "types", Meta("root-types", false),
            "{\"timestamp\":42,\"type\":{},\"payload\":{\"type\":[]}}",
            "{\"timestamp\":42,\"type\":\"event_msg\",\"payload\":{\"type\":42}}");

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
    }

    [Fact]
    public void ExplicitSubagentIsExcludedFromTheBoundedHeadWithoutReadingItsLargeTail()
    {
        using var directory = new TestDirectory();
        var baseMetadata = Meta("agent-long", true);
        var metadata = baseMetadata[..^2] + ",\"padding\":\"" + new string('x', 8 * 1024) + "\"}}";
        Write(directory.Root, "long-meta", metadata, Event("task_started", Now.AddMinutes(-1)));

        var result = new RecentActivitySource([directory.Root], new TestClock(Now)).Sample();

        Assert.Null(result.Sessions);
        Assert.False(result.IsPartial);
        Assert.Empty(Assert.IsType<RecentSessionReference[]>(result.References));
    }

    [Fact]
    public void CountsOnlyUniqueRecentlyMeasuredClaudeSessionIds()
    {
        var current = Claude("11111111-2222-4333-8444-555555555555", Now.AddMinutes(-2), Now.AddHours(-2));
        Observation[] observations =
        [
            current,
            current with { ObservedAt = Now },
            Claude("22222222-3333-4444-8555-666666666666", Now.AddMinutes(-16), Now),
            Claude("33333333-4444-4555-8666-777777777777", null, Now),
            Claude("44444444-5555-4666-8777-888888888888", Now.AddMinutes(2), Now),
            current with { Source = "not-a-session.json" },
            current with { Provider = "codex" },
        ];

        Assert.Equal(1, RecentActivitySource.CountClaudeSessions(observations, Now));
    }

    [Fact]
    public void ClaudeZeroIsUnknownRatherThanConfirmedAbsence() =>
        Assert.Null(RecentActivitySource.CountClaudeSessions([], Now));

    [Fact]
    public void ClaudeReferencesUseMeasuredTimeDeduplicateAndOnlyExposeContainedPaths()
    {
        using var allowed = new TestDirectory();
        using var outside = new TestDirectory();
        const string firstId = "11111111-2222-4333-8444-555555555555";
        const string secondId = "22222222-3333-4444-8555-666666666666";
        var firstPath = Path.Combine(allowed.Root, "nested", firstId + ".json");
        Observation[] observations =
        [
            Claude(firstId, Now.AddMinutes(-5), Now) with { Source = Path.Combine(allowed.Root, firstId + ".json") },
            Claude(firstId, Now.AddMinutes(-1), Now.AddHours(-4)) with { Source = firstPath },
            Claude(secondId, Now.AddMinutes(-2), Now) with { Source = Path.Combine(outside.Root, secondId + ".json") },
            Claude("33333333-4444-4555-8666-777777777777", Now.AddMinutes(-20), Now)
                with { Source = Path.Combine(allowed.Root, "33333333-4444-4555-8666-777777777777.json") },
        ];

        var references = RecentActivitySource.ClaudeReferences(observations, Now, allowed.Root);

        Assert.Collection(references,
            reference =>
            {
                Assert.Equal("claude", reference.Provider);
                Assert.Equal(firstId, reference.Id);
                Assert.Equal(Now.AddMinutes(-1), reference.ObservedAt);
                Assert.Equal(Path.GetFullPath(firstPath), reference.LogPath);
            },
            reference =>
            {
                Assert.Equal(secondId, reference.Id);
                Assert.Equal(Now.AddMinutes(-2), reference.ObservedAt);
                Assert.Null(reference.LogPath);
            });
        Assert.Equal(2, RecentActivitySource.CountClaudeSessions(observations, Now));
    }

    [Fact]
    public void ReferenceLogPathIsNeverSerialized()
    {
        var reference = new RecentSessionReference("codex", "root-0001", Now, @"C:\private\rollout.jsonl");

        var json = System.Text.Json.JsonSerializer.Serialize(reference, JsonSupport.Options);

        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log_path", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string root, string name, params string[] lines)
    {
        var day = Path.Combine(root, "2026", "10", "31");
        Directory.CreateDirectory(day);
        File.WriteAllText(RolloutPath(root, name), string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    private static string RolloutPath(string root, string name) =>
        Path.Combine(root, "2026", "10", "31", $"rollout-{name}.jsonl");

    private static string Meta(string id, bool subagent) => "{\"timestamp\":\"" + Now.AddMinutes(-5).ToString("O")
        + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"session_id\":\"" + id
        + "\",\"source\":" + (subagent ? "{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"parent-0001\"}}}" : "\"vscode\"") + "}}";

    private static string Event(string type, DateTimeOffset timestamp) => "{\"timestamp\":\"" + timestamp.ToString("O")
        + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"}}";

    private static Observation Claude(string sessionId, DateTimeOffset? measuredAt, DateTimeOffset observedAt) =>
        new("claude", Path.Combine("ignored", sessionId + ".json"), observedAt, measuredAt, []);
}
