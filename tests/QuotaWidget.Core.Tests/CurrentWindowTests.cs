using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class CurrentWindowTests
{
    [Theory]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    public void LatestClusterReplacesEarlierCycleRegardlessOfArrivalOrder(string provider, bool reverse)
    {
        var now = new DateTimeOffset(2026, 10, 30, 10, 31, 0, TimeSpan.Zero);
        var old = TestData.Window(provider, 23) with
        {
            MeasuredAt = new(2026, 10, 28, 6, 35, 0, TimeSpan.Zero),
            ObservedAt = now.AddHours(1), // Reading an old file later must not rejuvenate its measurement.
            ResetsAt = new(2026, 11, 2, 6, 50, 0, TimeSpan.Zero),
            Source = "old-cycle",
        };
        var recent = old with
        {
            UsedPercent = 5, MeasuredAt = now, ObservedAt = now,
            ResetsAt = new(2026, 11, 4, 6, 35, 0, TimeSpan.Zero), Source = "recent-cycle",
        };
        var sibling = recent with
        {
            UsedPercent = 8, ResetsAt = recent.ResetsAt?.AddSeconds(120),
            MeasuredAt = now.AddSeconds(-1), Source = "same-cluster",
        };
        Observation[] observations = [TestData.Observation(old), TestData.Observation(recent), TestData.Observation(sibling)];
        List<Diagnostic> diagnostics = [];
        var state = StateBuilder.Build(reverse ? observations.Reverse() : observations, diagnostics: diagnostics);
        Assert.Equal(recent, Assert.Single(provider == "codex" ? state.Codex.Windows : state.Claude.Windows));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(WindowAggregator.ReplacedDiagnosticCode, diagnostic.Code);
        Assert.Equal(Path.GetFullPath("old-cycle"), Path.GetFullPath(diagnostic.Source));
        Assert.Equal(1, diagnostic.Count);

        if (provider == "codex")
        {
            var claude = TestData.Provider("claude", 45) with
            {
                Windows = [TestData.Window(used: 45) with { MeasuredAt = now, ResetsAt = now.AddDays(3.5) }],
            };
            var verdict = Arbitrator.Decide(claude, state.Codex, null, now);
            Assert.Equal(5, verdict.Codex.Used);
            Assert.Equal(95 / ((recent.ResetsAt!.Value - now).TotalMinutes / 10080), verdict.Codex.Reserve, 6);
            Assert.Equal("equilibre", verdict.Verdict);
            Assert.Equal(4, verdict.Step);
            var obsolete = state.Codex with { Windows = [old] };
            Assert.Equal("gpt", Arbitrator.Decide(claude, obsolete, null, now).Verdict);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasurementThenObservationSelectsClusterEvenWhenNewestResetIsEarlier(bool undated)
    {
        var older = TestData.Window() with { MeasuredAt = undated ? null : TestData.Now };
        var newer = older with { ResetsAt = older.ResetsAt?.AddDays(-1), ObservedAt = TestData.Now.AddMinutes(1) };
        foreach (var windows in new[] { new[] { older, newer }, new[] { newer, older } })
        {
            Assert.Equal(newer, Assert.Single(StateBuilder.Build(windows.Select(TestData.Observation)).Claude.Windows));
        }
    }

    [Fact]
    public void DurationsCountersAndAccountsRemainIndependent()
    {
        var weekly = TestData.Window("codex");
        var shortWindow = weekly with { DurationMinutes = 300 };
        var otherCounter = weekly with { Counter = "other" };
        var otherAccount = TestData.Observation(weekly with { ResetsAt = weekly.ResetsAt?.AddDays(-1) })
            with { Account = JsonSupport.Fingerprint("account-b") };
        List<Diagnostic> diagnostics = [];
        var state = StateBuilder.Build([TestData.Observation(weekly), TestData.Observation(shortWindow),
            TestData.Observation(otherCounter), otherAccount], diagnostics: diagnostics);
        Assert.Equal(4, new[] { state.Codex }.Concat(state.OtherAccounts).Sum(p => p.Windows.Length));
        Assert.Single(state.OtherAccounts);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReplacementCountsClustersAndKeepsHistoricalBlockageEvidence()
    {
        var old = TestData.Window("codex", 100, measured: TestData.Now.AddMinutes(-1));
        var sibling = old with { ResetsAt = old.ResetsAt?.AddSeconds(120), Source = "sibling" };
        var recent = old with { UsedPercent = 5, MeasuredAt = TestData.Now, ResetsAt = old.ResetsAt?.AddDays(1) };
        List<Diagnostic> diagnostics = [];
        var state = StateBuilder.Build(new[] { old, sibling, recent }.Select(TestData.Observation), diagnostics: diagnostics);
        Assert.Equal(5, Assert.Single(state.Codex.Windows).UsedPercent);
        Assert.Equal("bloque", state.Codex.Blockage.State);
        Assert.Equal(1, Assert.Single(diagnostics).Count);
    }
}
