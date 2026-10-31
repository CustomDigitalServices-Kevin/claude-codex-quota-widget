using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.App.Tests;

public sealed partial class SessionDetailsTests
{
    private static readonly string[] InvalidRoots = ["", "."];

    [Fact]
    public void CodexUsesLatestContextAndLastIndexRenameWithoutTreatingRenameAsActivity()
    {
        using var fixture = new SessionFixture();
        var sessions = fixture.Directory("codex", "sessions");
        var logDirectory = fixture.Directory("codex", "sessions", "2026", "10", "31");
        const string id = "root-0001";
        var observed = DateTimeOffset.UtcNow.AddMinutes(-2);
        var created = observed.AddDays(-1);
        var firstContext = observed.AddMinutes(-5);
        var lastContext = observed.AddMinutes(-1);
        var log = fixture.Write(Path.Combine(logDirectory, "rollout-test.jsonl"),
            $$$"""{"type":"session_meta","timestamp":"2099-01-01T00:00:00Z","payload":{"id":"{{{id}}}","session_id":"{{{id}}}","timestamp":"{{{created:O}}}","cwd":"C:/initial","source":"vscode","originator":"codex","agent_role":"reviewer","agent_nickname":"Lynx"}}""",
            $$$"""{"type":"turn_context","timestamp":"{{{firstContext:O}}}","payload":{"model":"gpt-old","effort":"low","cwd":"C:/old"}}""",
            """{"type":"event_msg","payload":{"message":"SECRET_PROMPT"}}""",
            $$$"""{"type":"turn_context","timestamp":"{{{lastContext:O}}}","payload":{"model":"gpt-new","effort":"high","cwd":"C:/project"}}""");
        fixture.Write(Path.Combine(fixture.Path("codex"), "session_index.jsonl"),
            $$$"""{"id":"{{{id}}}","thread_name":"Old name","updated_at":"2000-01-01T00:00:00Z"}""",
            $$$"""{"id":"{{{id}}}","thread_name":"Renamed session","updated_at":"2099-01-01T00:00:00Z"}""");

        var detail = Assert.Single(new SessionDetailsSource([sessions], fixture.Path("state"), fixture.Path("projects"))
            .Read([new("codex", id, observed, log)]));

        Assert.Equal("Renamed session", detail.Name);
        Assert.Equal("gpt-new", detail.Model);
        Assert.Equal("high", detail.Effort);
        Assert.Equal("C:/project", detail.ProjectPath);
        Assert.Equal("codex", detail.Originator);
        Assert.Equal("reviewer", detail.AgentRole);
        Assert.Equal("Lynx", detail.AgentNickname);
        Assert.Equal(lastContext, detail.ContextAt);
        Assert.Equal(created, detail.CreatedAt);
        Assert.Equal(observed, detail.ObservedAt);
        Assert.DoesNotContain("SECRET_PROMPT", JsonSerializer.Serialize(detail), StringComparison.Ordinal);
    }

    [Fact]
    public void CodexRejectsSourcesOutsideRootMissingTruncatedMismatchedAndReparse()
    {
        using var fixture = new SessionFixture();
        var sessions = fixture.Directory("codex", "sessions");
        var inside = fixture.Directory("codex", "sessions", "day");
        var outside = fixture.Directory("outside");
        var now = DateTimeOffset.UtcNow;
        var validOutside = fixture.Write(Path.Combine(outside, "rollout-outside.jsonl"),
            """{"type":"session_meta","payload":{"id":"root-0001"}}""");
        var mismatched = fixture.Write(Path.Combine(inside, "rollout-mismatch.jsonl"),
            """{"type":"session_meta","payload":{"id":"root-OTHER"}}""");
        var truncated = Path.Combine(inside, "rollout-truncated.jsonl");
        File.WriteAllText(truncated, """{"type":"session_meta","payload":{"id":"root-0001"}}""");
        var source = new SessionDetailsSource([sessions], fixture.Path("state"), fixture.Path("projects"));

        var details = source.Read([
            new("codex", "root-0001", now, validOutside),
            new("codex", "root-0001", now, mismatched),
            new("codex", "root-0001", now, truncated),
            new("codex", "root-0001", now, Path.Combine(inside, "rollout-missing.jsonl")),
        ]);

        Assert.All(details, detail => Assert.Equal("Détails Codex indisponibles.", detail.Notice));
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(details), StringComparison.OrdinalIgnoreCase);

        var target = fixture.Write(Path.Combine(inside, "rollout-target.jsonl"),
            """{"type":"session_meta","payload":{"id":"root-0001"}}""");
        var link = Path.Combine(inside, "rollout-link.jsonl");
        try
        {
            File.CreateSymbolicLink(link, target);
            var linked = Assert.Single(source.Read([new("codex", "root-0001", now, link)]));
            Assert.Equal("Détails Codex indisponibles.", linked.Notice);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException) { }
    }

    [Fact]
    public void CodexCreationIsUnknownWhenPayloadTimestampIsAbsentInvalidOrFuture()
    {
        using var fixture = new SessionFixture();
        var sessions = fixture.Directory("codex", "sessions");
        var directory = fixture.Directory("codex", "sessions", "day");
        var now = DateTimeOffset.UtcNow;
        var absent = fixture.Write(Path.Combine(directory, "rollout-absent.jsonl"),
            """{"type":"session_meta","timestamp":"2020-01-01T00:00:00Z","payload":{"id":"root-absent"}}""");
        var invalid = fixture.Write(Path.Combine(directory, "rollout-invalid.jsonl"),
            """{"type":"session_meta","payload":{"id":"root-invalid","timestamp":"not-a-date"}}""");
        var future = fixture.Write(Path.Combine(directory, "rollout-future.jsonl"),
            """{"type":"session_meta","payload":{"id":"root-future","timestamp":"2099-01-01T00:00:00Z"}}""");

        var details = new SessionDetailsSource([sessions], fixture.Path("state"), fixture.Path("projects")).Read([
            new("codex", "root-absent", now, absent),
            new("codex", "root-invalid", now, invalid),
            new("codex", "root-future", now, future),
        ]);

        Assert.Equal(3, details.Length);
        Assert.All(details, detail => Assert.Null(detail.CreatedAt));
    }

    [Fact]
    public void CodexReadsCompleteTwentyTwoKilobyteHeaderFromJournalLargerThanOneMegabyte()
    {
        using var fixture = new SessionFixture();
        var sessions = fixture.Directory("codex", "sessions");
        var directory = fixture.Directory("codex", "sessions", "day");
        const string id = "root-long-header";
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var created = now.AddDays(-2);
        var header = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { id, timestamp = created.ToString("O", CultureInfo.InvariantCulture), padding = new string('x', 22_000) },
        });
        Assert.InRange(Encoding.UTF8.GetByteCount(header), 22_000, 23_000);
        var context = JsonSerializer.Serialize(new
        {
            type = "turn_context",
            timestamp = now.ToString("O", CultureInfo.InvariantCulture),
            payload = new { model = "gpt-test", effort = "high" },
        });
        var log = fixture.WriteRaw(Path.Combine(directory, "rollout-long.jsonl"),
            LargeJournal(header, context, SessionMetadataReader.MaximumFileBytes + 4096));

        var detail = Assert.Single(new SessionDetailsSource([sessions], fixture.Path("state"), fixture.Path("projects"))
            .Read([new("codex", id, now, log)]));

        Assert.Null(detail.Notice);
        Assert.Equal("gpt-test", detail.Model);
        Assert.Equal(created, detail.CreatedAt);
    }

    [Fact]
    public void HeaderBeyondTwoHundredFiftySixKilobytesFailsClosedAndCutJsonCannotAttributeAnotherId()
    {
        using var fixture = new SessionFixture();
        var sessions = fixture.Directory("codex", "sessions");
        var directory = fixture.Directory("codex", "sessions", "day");
        const string originalId = "root-original";
        const string otherId = "root-another";
        var now = DateTimeOffset.UtcNow;
        var header = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { id = originalId, padding = new string('x', 270_000), other_id = otherId },
        });
        Assert.True(Encoding.UTF8.GetByteCount(header) > 256 * 1024);
        var log = fixture.WriteRaw(Path.Combine(directory, "rollout-oversized.jsonl"),
            LargeJournal(header, "{}", SessionMetadataReader.MaximumFileBytes + 4096));
        var source = new SessionDetailsSource([sessions], fixture.Path("state"), fixture.Path("projects"));

        var details = source.Read([
            new("codex", originalId, now, log),
            new("codex", otherId, now, log),
        ]);

        Assert.Equal(2, details.Length);
        Assert.All(details, detail => Assert.Equal("Détails Codex indisponibles.", detail.Notice));
        Assert.All(details, detail => Assert.Null(detail.CreatedAt));
    }

    [Fact]
    public void ClaudeReadsOnlyExactRuntimeFieldsAndNeverExportsConversationContent()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var project = fixture.Directory("projects", "C--work");
        var id = Guid.NewGuid().ToString("D");
        var observed = DateTimeOffset.UtcNow.AddMinutes(-2);
        var first = observed.AddMinutes(-4);
        var latest = observed.AddMinutes(-1);
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), "{}");
        fixture.Write(Path.Combine(project, id + ".jsonl"),
            $$$"""{"type":"user","cwd":"SECRET_USER_CWD","message":{"content":"SECRET_USER"},"timestamp":"{{{first:O}}}"}""",
            $$$"""{"type":"assistant","cwd":"C:/old","effort":"low","timestamp":"{{{first:O}}}","message":{"model":"claude-old","content":[{"text":"SECRET_CONTENT"}]}}""",
            $$$"""{"type":"assistant","cwd":"C:/latest","timestamp":"{{{latest:O}}}","message":{"model":"claude-latest","content":[{"text":"SECRET_LATEST"}]},"params":{"model":"SECRET_TOOL_MODEL"},"wireIngestContext":{"model":"SECRET_WIRE"}}""",
            $$$"""{"type":"assistant","sessionId":"{{{Guid.NewGuid():D}}}","cwd":"SECRET_DISCORDANT","effort":"max","timestamp":"{{{latest.AddMinutes(1):O}}}","message":{"model":"SECRET_DISCORDANT_MODEL"}}""",
            $$$"""{"type":"ai-title","sessionId":"{{{Guid.NewGuid():D}}}","aiTitle":"SECRET_WRONG_TITLE"}""",
            $$$"""{"type":"ai-title","sessionId":"{{{id}}}","aiTitle":"Useful session"}""",
            """{"type":"assistant","cwd":"SECRET_PARTIAL"""
        );

        var detail = Assert.Single(new SessionDetailsSource([], state, projects)
            .Read([new("claude", id, observed, statePath)]));

        Assert.Equal("Useful session", detail.Name);
        Assert.Equal("claude-latest", detail.Model);
        Assert.Null(detail.Effort);
        Assert.Equal("C:/latest", detail.ProjectPath);
        Assert.Equal(latest, detail.ContextAt);
        Assert.Equal(ClaudeSessionDetails.LocalNotice, detail.Notice);
        Assert.Null(detail.CreatedAt);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(detail), StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeOfficialStateMetadataTakesPriorityOverRuntimeFallback()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var project = fixture.Directory("projects", "project");
        var id = Guid.NewGuid().ToString("D");
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), $$$"""
            {"sessionId":"{{{id}}}","session_name":"Official name","model":{"id":"official-model"},"effort":{"level":"max"},"cwd":"C:/official","agent":{"name":"Official agent"}}
            """);
        fixture.Write(Path.Combine(project, id + ".jsonl"),
            $$$"""{"type":"assistant","cwd":"C:/fallback","effort":"low","timestamp":"{{{observed:O}}}","message":{"model":"fallback-model"}}""");

        var detail = Assert.Single(new SessionDetailsSource([], state, projects)
            .Read([new("claude", id, observed, statePath)]));

        Assert.Equal("Official name", detail.Name);
        Assert.Equal("official-model", detail.Model);
        Assert.Equal("max", detail.Effort);
        Assert.Equal("C:/official", detail.ProjectPath);
        Assert.Equal("Official agent", detail.AgentNickname);
    }

    [Fact]
    public void ClaudeFailsClosedForOutsideStateDiscordantUuidAndAmbiguousExactRuntime()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var outside = fixture.Directory("outside");
        var id = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var outsidePath = fixture.WriteRaw(Path.Combine(outside, id + ".json"), "{}");
        var discordant = fixture.WriteRaw(Path.Combine(state, id + ".json"),
            $$$"""{"sessionId":"{{{other}}}"}""");
        var malformedIdState = fixture.WriteRaw(Path.Combine(state, other + ".json"), "{");
        fixture.Write(Path.Combine(fixture.Directory("projects", "one"), other + ".jsonl"),
            $$$"""{"type":"assistant","timestamp":"{{{now:O}}}","message":{"model":"one"}}""");
        fixture.Write(Path.Combine(fixture.Directory("projects", "two"), other + ".jsonl"),
            $$$"""{"type":"assistant","timestamp":"{{{now:O}}}","message":{"model":"two"}}""");
        var source = new SessionDetailsSource([], state, projects);

        var details = source.Read([
            new("claude", id, now, outsidePath),
            new("claude", id, now, discordant),
            new("claude", other, now, malformedIdState),
            new("claude", "not-a-uuid", now, malformedIdState),
        ]);

        Assert.All(details, detail => Assert.Equal("Détails Claude indisponibles.", detail.Notice));
    }

    [Fact]
    public void ReadHonorsCancellationRecentWindowAndThirtyTwoSessionLimit()
    {
        using var fixture = new SessionFixture();
        var source = new SessionDetailsSource([], fixture.Path("state"), fixture.Path("projects"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Read([
            new("codex", "root-0001", DateTimeOffset.UtcNow, null)], cancellation.Token));

        var now = DateTimeOffset.UtcNow;
        var references = Enumerable.Range(0, 40)
            .Select(index => new RecentSessionReference("codex", $"root-{index:0000}", now, null))
            .Append(new("codex", "root-old00", now.AddMinutes(-16), null));
        var details = source.Read(references);
        Assert.Equal(32, details.Length);
        Assert.DoesNotContain(details, detail => detail.Id == "root-old00");
    }

    [Fact]
    public void EmptyOrRelativeAllowedRootsNeverAuthorizeFilesUnderCurrentDirectory()
    {
        var existing = typeof(SessionDetailsTests).Assembly.Location;
        Assert.True(Path.IsPathFullyQualified(existing));
        Assert.False(SessionMetadataReader.TryResolveRegularFile(existing, InvalidRoots, out _, out _));

        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var id = Guid.NewGuid().ToString("D");
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), "{}");
        var detail = Assert.Single(new SessionDetailsSource([], "", fixture.Path("projects"))
            .Read([new("claude", id, DateTimeOffset.UtcNow, statePath)]));
        Assert.Equal("Détails Claude indisponibles.", detail.Notice);
    }

}
