using System.Diagnostics;
using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed partial class CodexActiveTests
{
    [Fact]
    public async Task MissingBinaryAndInvalidStateNeverLaunch()
    {
        using var directory = new TestDirectory();
        var options = new CodexActiveOptions(directory.PathFor("data"), directory.PathFor("missing.exe"))
        { UserProfileForTests = directory.PathFor("user") };
        Assert.Equal("codex_introuvable", (await new CodexActiveSource(options, TestData.Clock).ReadAsync()).Diagnostic?.Code);
        directory.Write("data/codex-active-state.json", "broken");
        Assert.Equal("etat_lecture_directe_illisible", (await new CodexActiveSource(options, TestData.Clock).ReadAsync()).Diagnostic?.Code);
    }

    [Fact]
    public async Task MissingAccountFallsBackOnlyToMostRecentDedicatedSession()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, result: FakeCodex.Result.Replace("\"accountId\":\"account-a\",", "", StringComparison.Ordinal));
        directory.Write("data/codex-home/sessions/2026/10/30/rollout-test.jsonl", TestData.Metadata("dedicated-account") + "\n" + TestData.Journal() + "\n");
        var result = await new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock).ReadAsync();
        Assert.Equal(JsonSupport.Fingerprint("dedicated-account"), Assert.Single(result.Observations).Account);
    }

    [Fact]
    public async Task UnknownActiveAccountRemainsSeparateAndFailedActiveReadKeepsPassiveData()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, result: FakeCodex.Result.Replace("\"accountId\":\"account-a\",", "", StringComparison.Ordinal));
        var source = new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock);
        var first = await new QuotaCollector(TestData.Clock, source).CollectAsync(directory.PathFor("claude"), []);
        Assert.Empty(StateBuilder.Build(first.Observations).Codex.Windows);
        Assert.Single(StateBuilder.Build(first.Observations).OtherAccounts);
        var passive = TestData.ParseJournal(TestData.Journal());
        var previous = first with { Observations = [.. first.Observations, passive] };
        var second = await new QuotaCollector(TestData.Clock, source).CollectAsync(directory.PathFor("claude"), [], previous);
        Assert.Contains(second.Observations, o => o == passive);
        Assert.Contains(second.Observations, o => o.Source == "app-server");
        Assert.Contains(second.Diagnostics, d => d.Code == "lecture_directe_trop_recente");
    }

    [Fact]
    public async Task ActiveBlockageSurvivesNewerPassiveReadingUntilExplicitRecovery()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, result: FakeCodex.Result.Replace("\"ordinaryUsageAllowed\":true", "\"ordinaryUsageAllowed\":false", StringComparison.Ordinal));
        var source = new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock);
        var result = await source.ReadAsync();
        Assert.Null(result.Diagnostic);
        var passive = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(1), reachedPresent: false));
        var state = StateBuilder.Build([.. result.Observations, passive]);
        Assert.Equal("bloque", state.Codex.Blockage.State);
        Assert.False(state.Codex.OrdinaryUsageAllowed);
        Assert.Equal(2, state.Codex.ResetCredits?.AvailableCount);
        Assert.Equal(TestData.Now, state.Codex.ActiveMeasuredAt);
    }

    [Fact]
    public async Task ActiveCacheIsSharedAcrossCollectorsWithoutSnapshotAndKeepsOriginalAge()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory);
        var options = FakeCodex.Options(directory, scenario);
        var first = await new CodexActiveSource(options, TestData.Clock).ReadAsync();
        var later = new TestClock(TestData.Now.AddMinutes(1));
        var second = await new QuotaCollector(later, new(options, later)).CollectAsync(directory.PathFor("claude"), []);
        var active = Assert.Single(second.Observations);
        Assert.Equal(first.Observations[0].MeasuredAt, active.MeasuredAt);
        Assert.Equal(TestData.Now, active.MeasuredAt);
        Assert.Contains(second.Diagnostics, d => d.Code == "lecture_directe_trop_recente");
        Assert.Equal(2, FakeCodex.Launches(scenario));
        var saved = File.ReadAllText(Path.Combine(options.DataDirectory, "codex-active-state.json"));
        Assert.DoesNotContain("account-a", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulResponseStillKillsServerThatIgnoresClosedStdin()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, finalExtras: "\"hang\":true");
        var result = await new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock).ReadAsync();
        Assert.Null(result.Diagnostic);
        var pid = int.Parse(File.ReadAllLines(scenario + ".pids")[^1], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException) { /* The process has already been reaped. */ }
    }

    [Fact]
    public async Task LoginTimeoutCannotStartAppServer()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory, login: "{\"exitCode\":0,\"hang\":true}");
        var options = FakeCodex.Options(directory, scenario) with { TotalTimeout = TimeSpan.FromSeconds(1) };
        var result = await new CodexActiveSource(options, TestData.Clock).ReadAsync();
        Assert.Equal("lecture_directe_delai_depasse", result.Diagnostic?.Code);
        Assert.Equal(1, FakeCodex.Launches(scenario));
    }

    [Fact]
    public async Task StderrIsDrainedConcurrentlyAndNeverPassedToMethodLogger()
    {
        using var directory = new TestDirectory();
        var scenario = FakeCodex.Scenario(directory,
            finalExtras: "\"stderr\":" + JsonSerializer.Serialize("private-stderr-" + new string('x', 131072)));
        List<string> names = [];
        var result = await new CodexActiveSource(FakeCodex.Options(directory, scenario), TestData.Clock,
            names.Add, names.Add).ReadAsync();
        Assert.Null(result.Diagnostic);
        Assert.Equal(5, names.Count);
        Assert.DoesNotContain(names, name => name.Contains("private-stderr", StringComparison.Ordinal)
            || name.Contains("hidden", StringComparison.Ordinal));
    }
}
