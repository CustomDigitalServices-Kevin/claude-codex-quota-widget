using System.Globalization;
using System.Text.Json;
using QuotaWidget.Core;
using Xunit.Abstractions;

namespace QuotaWidget.Core.Tests;

public sealed class PassiveSourceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("rollout-app-sample.jsonl", 12, "Codex Desktop", "vscode",
        "00000000-0000-4000-8000-000000000101", "2026-10-29T09:30:00Z")]
    [InlineData("rollout-cli-sample.jsonl", 24, "codex_exec", "cli",
        "00000000-0000-4000-8000-000000000102", "2026-10-30T09:45:00Z")]
    public void SyntheticJournalsReadMetadataQuotasAndIgnoreTruncatedLastLine(string name, double used,
        string originator, string source, string sessionId, string measuredAt)
    {
        var path = TestData.Fixture("codex", name);
        using (var head = JsonDocument.Parse(File.ReadLines(path).First()))
        {
            var payload = head.RootElement.GetProperty("payload");
            Assert.Equal("session_meta", head.RootElement.GetProperty("type").GetString());
            Assert.Equal(sessionId, payload.GetProperty("id").GetString());
            Assert.Equal(sessionId, payload.GetProperty("session_id").GetString());
            Assert.Equal(source, payload.GetProperty("source").GetString());
            Assert.Equal("c:/work/example", payload.GetProperty("cwd").GetString());
            Assert.Equal("00000000-0000-4000-8000-000000000001",
                payload.GetProperty("creator_account_id").GetString());
        }
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(path);
        var observation = Assert.Single(result.Observations);
        var state = StateBuilder.Build(result.Observations);
        var window = Assert.Single(state.Codex.Windows);
        Assert.Equal(used, window.UsedPercent);
        Assert.Equal(10080, window.DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse(measuredAt, CultureInfo.InvariantCulture), observation.MeasuredAt);
        Assert.All(result.Observations, o => Assert.Equal(originator, o.Originator));
        Assert.Equal(JsonSupport.Fingerprint("00000000-0000-4000-8000-000000000001"), state.Codex.Account);
        Assert.Equal(42.5, state.Codex.Credits?.Balance);
        Assert.Contains(result.Diagnostics, d => d.Code is "codex_lignes_invalides" or "codex_ligne_incomplete");
    }

    [Fact]
    public void ClaudeReadsAllStrictNamesAndDiagnosesInvalidJson()
    {
        using var directory = new TestDirectory();
        var json = File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json"));
        directory.Write("valid-1.json", json);
        directory.Write("valid_2.json", json);
        directory.Write("bad.name.json", json);
        directory.Write("invalid.json", "{truncated");
        directory.Write("not-json.txt", json);
        var result = new ClaudeSource(TestData.Clock).Read(directory.Root);
        Assert.Equal(2, result.Observations.Length);
        Assert.Equal("claude_fichier_ignore", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MissingDirectoriesAreEmptyWithoutExceptions()
    {
        using var directory = new TestDirectory();
        var missing = directory.PathFor("absent");
        Assert.Empty(new ClaudeSource(TestData.Clock).Read(missing).Observations);
        Assert.Empty(new CodexPassiveSource(TestData.Clock).Read([missing]).Observations);
        Assert.Empty(new ClaudeSource(TestData.Clock).Purge(missing).Diagnostics);
    }

    [Fact]
    public void BothProfilesAreDiscoveredAndNewestObservationWins()
    {
        using var directory = new TestDirectory();
        var app = directory.PathFor("app");
        var cli = directory.PathFor("cli");
        directory.Write("app/2026/10/29/rollout-app.jsonl", File.ReadAllText(TestData.Fixture("codex", "rollout-app-sample.jsonl")));
        directory.Write("cli/2026/10/30/rollout-cli.jsonl", File.ReadAllText(TestData.Fixture("codex", "rollout-cli-sample.jsonl")));
        var result = new CodexPassiveSource(TestData.Clock).Read([app, cli]);
        Assert.Contains(result.Observations, o => o.Originator == "Codex Desktop");
        Assert.Contains(result.Observations, o => o.Originator == "codex_exec");
        var appObservation = Assert.Single(result.Observations, o => o.Originator == "Codex Desktop");
        var cliObservation = Assert.Single(result.Observations, o => o.Originator == "codex_exec");
        Assert.True(cliObservation.MeasuredAt > appObservation.MeasuredAt);
        Assert.Equal(appObservation.Account, cliObservation.Account);
        Assert.Equal(24, Assert.Single(StateBuilder.Build(result.Observations).Codex.Windows).UsedPercent);
    }

    [Fact]
    public void DiscoveryUsesEightCalendarDaysAndFiftyNewestFilesPerProfile()
    {
        using var directory = new TestDirectory();
        var content = TestData.Metadata() + "\n" + TestData.Journal() + "\n";
        for (var index = 0; index < 55; index++)
        {
            var path = directory.Write(string.Create(CultureInfo.InvariantCulture, $"2026/10/30/rollout-{index:D2}.jsonl"), content);
            File.SetLastWriteTimeUtc(path, TestData.Now.UtcDateTime.AddMinutes(index));
        }
        var oldestIncludedDay = directory.Write("2026/10/23/rollout-boundary.jsonl", content);
        File.SetLastWriteTimeUtc(oldestIncludedDay, TestData.Now.UtcDateTime.AddHours(2));
        var excludedDay = directory.Write("2026/10/22/rollout-old.jsonl", content);
        File.SetLastWriteTimeUtc(excludedDay, TestData.Now.UtcDateTime.AddHours(3));
        directory.Write("2026/10/31/rollout-future.jsonl", content);
        var result = new CodexPassiveSource(TestData.Clock).Read([directory.Root]);
        Assert.Equal(50, result.Observations.Length);
        Assert.Contains(result.Observations, o => Path.GetFullPath(o.Source) == Path.GetFullPath(oldestIncludedDay));
        Assert.DoesNotContain(result.Observations, o => Path.GetFullPath(o.Source) == Path.GetFullPath(excludedDay));
        Assert.DoesNotContain(result.Observations, o => o.Source.EndsWith("rollout-00.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void TailExpandsToFindEventAndReadsMetadataSeparately()
    {
        using var directory = new TestDirectory();
        var paddingLine = "{\"type\":\"response_item\",\"padding\":\"" + new string('x', 1024) + "\"}\n";
        var content = TestData.Metadata() + "\n" + TestData.Journal(7.25) + "\n"
            + string.Concat(Enumerable.Repeat(paddingLine, 130));
        var path = directory.Write("rollout-expanded.jsonl", content);
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(path);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(7.25, observation.Windows[0].UsedPercent);
        Assert.Equal(JsonSupport.Fingerprint("account-a"), observation.Account);
    }

    [Fact]
    public void TailNeverSearchesBeyondOneMebibyte()
    {
        using var directory = new TestDirectory();
        var paddingLine = "{\"type\":\"response_item\",\"padding\":\"" + new string('x', 1024) + "\"}\n";
        var content = TestData.Metadata() + "\n" + TestData.Journal() + "\n"
            + string.Concat(Enumerable.Repeat(paddingLine, 1100));
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(directory.Write("rollout-bounded.jsonl", content));
        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, d => d.Code == "codex_mesure_absente_dans_borne");
    }

    [Fact]
    public void OversizedFirstLineMakesAccountUnknownWithoutSearchingOtherMetadataLines()
    {
        using var directory = new TestDirectory();
        var head = "{\"type\":\"session_meta\",\"padding\":\"" + new string('x', 512 * 1024) + "\"}\n";
        var content = head + TestData.Metadata() + "\n" + TestData.Journal() + "\n";
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(directory.Write("rollout-big-head.jsonl", content));
        Assert.Null(Assert.Single(result.Observations).Account);
        Assert.Contains(result.Diagnostics, d => d.Code == "codex_entete_trop_longue");
        Assert.Empty(StateBuilder.Build(result.Observations).Codex.Windows);
        Assert.Single(StateBuilder.Build(result.Observations).OtherAccounts);
    }

    [Fact]
    public void InvalidLinesAndIncompleteValidJsonDoNotHideEarlierCompleteEvent()
    {
        using var directory = new TestDirectory();
        var content = TestData.Metadata() + "\n" + TestData.Journal(3) + "\n{broken}\n" + TestData.Journal(80);
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(directory.Write("rollout-incomplete.jsonl", content));
        Assert.Equal(3, Assert.Single(result.Observations).Windows[0].UsedPercent);
        Assert.Contains(result.Diagnostics, d => d.Code == "codex_lignes_invalides");
        Assert.Contains(result.Diagnostics, d => d.Code == "codex_ligne_incomplete");
    }

    [Fact]
    public void TailKeepsBlockageEvidenceBeforeLatestSparseEvent()
    {
        using var directory = new TestDirectory();
        var content = TestData.Metadata() + "\n"
            + TestData.Journal(at: TestData.Now.AddMinutes(-1), reached: "rate_limit_reached") + "\n"
            + TestData.Journal(reachedPresent: false) + "\n";
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(directory.Write("rollout-blocked.jsonl", content));
        Assert.Equal(2, result.Observations.Length);
        Assert.Equal("bloque", StateBuilder.Build(result.Observations).Codex.Blockage.State);
    }

    [Fact]
    public void EarlierCyclesAreBlockageEvidenceButOnlyLastEventSuppliesDisplayedWindows()
    {
        using var directory = new TestDirectory();
        var earlier = TestData.Journal(100, at: TestData.Now.AddMinutes(-1), reached: "rate_limit_reached");
        var oldReset = TestData.Now.AddDays(4).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        earlier = earlier.Replace(TestData.Now.AddDays(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            oldReset, StringComparison.Ordinal);
        var content = TestData.Metadata() + "\n" + earlier + "\n" + TestData.Journal(3, reachedPresent: false) + "\n";
        var result = new CodexPassiveSource(TestData.Clock).ReadFile(directory.Write("rollout-cycle.jsonl", content));
        Assert.True(result.Observations[0].IsHistorical);
        Assert.False(result.Observations[1].IsHistorical);
        var state = StateBuilder.Build(result.Observations).Codex;
        Assert.Equal(3, Assert.Single(state.Windows).UsedPercent);
        Assert.Equal("bloque", state.Blockage.State);
    }

    [Fact]
    public void PurgeUsesWeeklyResetOtherwiseFileAgeAndStrictNames()
    {
        using var directory = new TestDirectory();
        static string Weekly(DateTimeOffset reset) => string.Create(CultureInfo.InvariantCulture,
            $"{{\"rate_limits\":{{\"seven_day\":{{\"resets_at\":{reset.ToUnixTimeSeconds()}}}}}}}");
        var expired = directory.Write("expired.json", Weekly(TestData.Now.AddDays(-1).AddSeconds(-1)));
        var boundary = directory.Write("boundary.json", Weekly(TestData.Now.AddDays(-1)));
        var future = directory.Write("future.json", Weekly(TestData.Now.AddDays(1)));
        File.SetLastWriteTimeUtc(future, TestData.Now.UtcDateTime.AddDays(-20));
        var oldUnreadable = directory.Write("old.json", "{broken");
        File.SetLastWriteTimeUtc(oldUnreadable, TestData.Now.UtcDateTime.AddDays(-9));
        var missingWeek = directory.Write("missing.json", "{}");
        File.SetLastWriteTimeUtc(missingWeek, TestData.Now.UtcDateTime.AddDays(-9));
        var freshUnreadable = directory.Write("fresh.json", "{broken");
        var wrongName = directory.Write("wrong.name.json", Weekly(TestData.Now.AddDays(-3)));
        new ClaudeSource(TestData.Clock).Purge(directory.Root);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(oldUnreadable));
        Assert.False(File.Exists(missingWeek));
        Assert.True(File.Exists(boundary));
        Assert.True(File.Exists(future));
        Assert.True(File.Exists(freshUnreadable));
        Assert.True(File.Exists(wrongName));
    }

    [Fact]
    public void ClaudeReaderAndPurgeHandleRegularFilesWithoutLinkPrivileges()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("session.json",
            File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json")));
        Assert.Single(new ClaudeSource(TestData.Clock).Read(directory.Root).Observations);
        new ClaudeSource(TestData.Clock).Purge(directory.Root);
        Assert.True(File.Exists(path));
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, TestData.Now.UtcDateTime.AddDays(-20));
        new ClaudeSource(TestData.Clock).Purge(directory.Root);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ClaudeReaderAndPurgeIgnoreSymbolicLinks()
    {
        using var directory = new TestDirectory();
        var target = directory.Write("target/payload.txt",
            File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json")));
        var claudeDirectory = directory.PathFor("claude");
        Directory.CreateDirectory(claudeDirectory);
        File.SetLastWriteTimeUtc(target, TestData.Now.UtcDateTime.AddDays(-20));
        var link = Path.Combine(claudeDirectory, "session.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            output.WriteLine($"Symbolic-link assertions not executed: {exception.Message}");
            return;
        }
        Assert.Empty(new ClaudeSource(TestData.Clock).Read(claudeDirectory).Observations);
        File.WriteAllText(target, "{}");
        File.SetLastWriteTimeUtc(target, TestData.Now.UtcDateTime.AddDays(-20));
        new ClaudeSource(TestData.Clock).Purge(claudeDirectory);
        Assert.True(File.Exists(link));
        Assert.True(File.Exists(target));
    }
}
