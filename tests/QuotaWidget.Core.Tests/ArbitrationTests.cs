using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class ArbitrationTests
{
    [Theory]
    [InlineData(1, "gpt", 3)]
    [InlineData(2, "claude", 3)]
    [InlineData(3, "claude", 3)]
    [InlineData(4, "gpt", 1)]
    [InlineData(5, "claude", 1)]
    [InlineData(6, "aucun", 1)]
    [InlineData(7, "equilibre", 4)]
    [InlineData(8, "equilibre", 4)]
    [InlineData(9, "claude", 1)]
    [InlineData(10, "equilibre", 4)]
    [InlineData(11, "gpt", 3)]
    public void AllElevenPlanCasesHaveTheExpectedVerdictAndStep(int example, string verdict, int step)
    {
        var (claude, codex) = example switch
        {
            1 => (TestData.Provider("claude", 30, 0.9), TestData.Provider("codex", 40, 0.1)),
            2 => (TestData.Provider("claude", 60, 0.1), TestData.Provider("codex", 20, 0.9)),
            3 => (TestData.Provider("claude", 85, 0.05, fiveHour: 10), TestData.Provider("codex", 30, 0.9)),
            4 => (TestData.Provider("claude", 90), TestData.Provider("codex", 2)),
            5 => (TestData.Provider("claude", 50, fiveHour: 88), TestData.Provider("codex", 3, blocked: true)),
            6 => (TestData.Provider("claude", blocked: true), TestData.Provider("codex", blocked: true)),
            7 => (TestData.Provider("claude", null), TestData.Provider("codex", null)),
            8 => (TestData.Provider("claude", 50, 0.5), TestData.Provider("codex", 45, 0.55)),
            9 => (TestData.Provider("claude", 40), BlockedAfterAbsentField()),
            10 => (TestData.Provider("claude", 68, 0.348), TestData.Provider("codex", null)),
            11 => (TestData.Provider("claude", null), TestData.Provider("codex", 4, 0.697)),
            _ => throw new ArgumentOutOfRangeException(nameof(example)),
        };
        var result = Arbitrator.Decide(claude, codex, null, TestData.Now);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(step, result.Step);
        if (example is 7 or 10 or 11)
        {
            Assert.True(result.Uncertain);
        }
        if (example == 7)
        {
            Assert.True(result.Claude.Eligible);
            Assert.True(result.Codex.Eligible);
            Assert.Equal(100, result.Claude.Reserve);
            Assert.Equal(100, result.Codex.Reserve);
        }
    }

    private static ProviderState BlockedAfterAbsentField()
    {
        var blocked = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(-2), reached: "rate_limit_reached"));
        var missing = TestData.ParseJournal(TestData.Journal(reachedPresent: false));
        return StateBuilder.Build([blocked, missing]).Codex;
    }

    [Theory]
    [InlineData("equilibre", 24.9, "equilibre", 4)]
    [InlineData("equilibre", 25, "gpt", 3)]
    [InlineData("gpt", 20, "gpt", 3)]
    [InlineData("gpt", 19.9, "equilibre", 4)]
    [InlineData("claude", 23, "gpt", 3)]
    public void HysteresisUsesUnroundedEntryAndExitThresholds(string previous, double used, string verdict, int step)
    {
        var result = Arbitrator.Decide(TestData.Provider("claude", used, 1), TestData.Provider("codex", 0, 1),
            new(previous, previous == "equilibre" ? 4 : 3, TestData.Now.AddHours(-1)), TestData.Now);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(step, result.Step);
    }

    [Theory]
    [InlineData(24, 3)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(-1, 3)]
    public void ExpiredFutureAndEarlyStepHistoryDoNotApplyHysteresis(int ageHours, int previousStep)
    {
        var result = Arbitrator.Decide(TestData.Provider("claude", 23, 1), TestData.Provider("codex", 0, 1),
            new("gpt", previousStep, TestData.Now.AddHours(-ageHours)), TestData.Now);
        Assert.Equal("equilibre", result.Verdict);
        Assert.Equal(4, result.Step);
    }

    [Theory]
    [InlineData(85, 2)]
    [InlineData(89.99, 2)]
    [InlineData(90, 1)]
    public void EligibilityPrecedesFiveHourUrgency(double fiveHour, int step)
    {
        var result = Arbitrator.Decide(TestData.Provider("claude", 50, fiveHour: fiveHour),
            TestData.Provider("codex", 50), null, TestData.Now);
        Assert.Equal("gpt", result.Verdict);
        Assert.Equal(step, result.Step);
    }

    [Theory]
    [InlineData(21600, false)]
    [InlineData(21601, true)]
    public void SixHourAgeBoundaryIsStrict(int ageSeconds, bool uncertain)
    {
        var claude = TestData.Provider("claude");
        claude = claude with { Windows = [claude.Windows[0] with { MeasuredAt = TestData.Now.AddSeconds(-ageSeconds) }] };
        Assert.Equal(uncertain, Arbitrator.Decide(claude, TestData.Provider("codex"), null, TestData.Now).Uncertain);
    }

    [Fact]
    public void UnknownMeasurementIsUncertainWithoutInventingAnAge()
    {
        var claude = TestData.Provider("claude");
        claude = claude with { Windows = [claude.Windows[0] with { MeasuredAt = null }] };
        var result = Arbitrator.Decide(claude, TestData.Provider("codex"), null, TestData.Now);
        Assert.True(result.Uncertain);
        Assert.Null(result.Claude.AgeSeconds);
    }

    [Fact]
    public void UnknownUsageDoesNotInventAWeeklyMeasurement()
    {
        var claude = TestData.Provider("claude");
        claude = claude with { Windows = [claude.Windows[0] with { UsedPercent = null }] };
        var result = Arbitrator.Decide(claude, TestData.Provider("codex"), null, TestData.Now);
        Assert.True(result.Uncertain);
        Assert.Equal(0, result.Claude.Used);
        Assert.Equal(100, result.Claude.Reserve);
        Assert.Null(result.Claude.AgeSeconds);
        Assert.Null(result.Claude.RemainingFraction);
    }

    [Fact]
    public void PastResetUsesZeroAsLowerBoundButDoesNotEraseBlockage()
    {
        var claude = TestData.Provider("claude", 99, -0.01);
        var result = Arbitrator.Decide(claude, TestData.Provider("codex"), null, TestData.Now);
        Assert.True(result.Uncertain);
        Assert.True(result.Claude.Eligible);
        Assert.Equal(0, result.Claude.Used);
        Assert.Equal(2000, result.Claude.Reserve);
        var blocked = Arbitrator.Decide(claude with { Blockage = new("bloque", TestData.Now.AddDays(-1), "fixture") },
            TestData.Provider("codex"), null, TestData.Now);
        Assert.False(blocked.Claude.Eligible);
        Assert.Equal(1, blocked.Step);
    }

    [Fact]
    public void MissingDurationOrResetUsesRawReserve()
    {
        var codex = TestData.Provider("codex", 20);
        foreach (var window in new[] { codex.Windows[0] with { DurationMinutes = null }, codex.Windows[0] with { ResetsAt = null } })
        {
            var result = Arbitrator.Decide(TestData.Provider("claude"), codex with { Windows = [window] }, null, TestData.Now);
            Assert.True(result.Uncertain);
            Assert.Equal(80, result.Codex.Reserve);
            Assert.Null(result.Codex.RemainingFraction);
        }
    }

    [Fact]
    public void CodexUsesWeeklyWindowOtherwiseLongestDuration()
    {
        var codex = TestData.Provider("codex") with
        {
            Windows = [TestData.Window("codex", 20, duration: 20000), TestData.Window("codex", 30, duration: 10080)],
        };
        Assert.Equal(30, Arbitrator.Decide(TestData.Provider("claude"), codex, null, TestData.Now).Codex.Used);
        codex = codex with { Windows = [TestData.Window("codex", 20, duration: 600), TestData.Window("codex", 30, duration: 300)] };
        Assert.Equal(20, Arbitrator.Decide(TestData.Provider("claude"), codex, null, TestData.Now).Codex.Used);
    }
}
