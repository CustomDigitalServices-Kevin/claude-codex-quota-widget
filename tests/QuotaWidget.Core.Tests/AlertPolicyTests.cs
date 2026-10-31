using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class AlertPolicyTests
{
    private static QuotaState State(WindowObservation window) => StateBuilder.Build([TestData.Observation(window)]);

    [Fact]
    public void EmitsEightyThenNinetyFiveOnceEachAndDoesNotMutateInput()
    {
        var window = TestData.Window(used: 80);
        var initial = new AlertState([], TestData.Now);
        var first = AlertPolicy.Evaluate(State(window), initial, TestData.Now);
        Assert.Equal(80, Assert.Single(first.Alerts).Threshold);
        Assert.Empty(initial.Thresholds);
        Assert.Empty(AlertPolicy.Evaluate(State(window with { UsedPercent = 94 }), first.State, TestData.Now).Alerts);
        var second = AlertPolicy.Evaluate(State(window with { UsedPercent = 95 }), first.State, TestData.Now);
        Assert.Equal(95, Assert.Single(second.Alerts).Threshold);
        Assert.Empty(AlertPolicy.Evaluate(State(window with { UsedPercent = 99 }), second.State, TestData.Now).Alerts);
        Assert.Empty(AlertPolicy.Evaluate(State(window with { UsedPercent = 80 }), second.State, TestData.Now).Alerts);
    }

    [Fact]
    public void DirectJumpEmitsOnlyHighestThreshold()
    {
        var result = AlertPolicy.Evaluate(State(TestData.Window(used: 99)), null, TestData.Now);
        Assert.Equal(95, Assert.Single(result.Alerts).Threshold);
    }

    [Fact]
    public void OldUnknownFutureAndExpiredMeasurementsDoNotEmitThresholdAlerts()
    {
        var window = TestData.Window(used: 99);
        foreach (var measurement in new DateTimeOffset?[] { null, TestData.Now.AddMinutes(-31), TestData.Now.AddSeconds(1) })
        { Assert.Empty(AlertPolicy.Evaluate(State(window with { MeasuredAt = measurement }), null, TestData.Now).Alerts); }
        Assert.Empty(AlertPolicy.Evaluate(State(window with { ResetsAt = TestData.Now }), null, TestData.Now).Alerts);
        Assert.Single(AlertPolicy.Evaluate(State(window with { MeasuredAt = TestData.Now.AddMinutes(-30) }), null, TestData.Now).Alerts);
    }

    [Fact]
    public void CanonicalResetClusterAndCounterSeparateThresholds()
    {
        var window = TestData.Window(used: 80);
        var first = AlertPolicy.Evaluate(State(window), null, TestData.Now);
        Assert.Empty(AlertPolicy.Evaluate(State(window with { ResetsAt = window.ResetsAt!.Value.AddSeconds(120) }), first.State, TestData.Now).Alerts);
        Assert.Single(AlertPolicy.Evaluate(State(window with { ResetsAt = window.ResetsAt!.Value.AddSeconds(121) }), first.State, TestData.Now).Alerts);
        Assert.Single(AlertPolicy.Evaluate(State(window with { Counter = "other-counter" }), first.State, TestData.Now).Alerts);
        Assert.Single(AlertPolicy.Evaluate(State(window with { Type = "other-window" }), first.State, TestData.Now).Alerts);
        var within = AlertPolicy.Evaluate(State(window with { ResetsAt = window.ResetsAt!.Value.AddSeconds(100) }), first.State, TestData.Now);
        Assert.Single(AlertPolicy.Evaluate(State(window with { ResetsAt = window.ResetsAt!.Value.AddSeconds(200) }), within.State, TestData.Now).Alerts);
    }

    [Fact]
    public void ClaudeAbsenceEmitsOnceAndRearmsAtNextMeasurement()
    {
        var window = TestData.Window(used: 10, measured: TestData.Now.AddHours(-6));
        var first = AlertPolicy.Evaluate(State(window), null, TestData.Now);
        Assert.Null(Assert.Single(first.Alerts).Threshold);
        Assert.Empty(AlertPolicy.Evaluate(State(window), first.State, TestData.Now.AddHours(1)).Alerts);
        var renewed = AlertPolicy.Evaluate(State(window with { MeasuredAt = TestData.Now }), first.State, TestData.Now);
        Assert.Empty(renewed.Alerts);
        var again = AlertPolicy.Evaluate(State(window with { MeasuredAt = TestData.Now }), renewed.State, TestData.Now.AddHours(6));
        Assert.Null(Assert.Single(again.Alerts).Window);
    }

    [Fact]
    public void NoClaudeHistoryStartsSixHourClockAndPersistsIt()
    {
        var first = AlertPolicy.Evaluate(StateBuilder.Build([]), null, TestData.Now);
        Assert.Empty(first.Alerts);
        Assert.Empty(AlertPolicy.Evaluate(StateBuilder.Build([]), first.State, TestData.Now.AddHours(5)).Alerts);
        Assert.Single(AlertPolicy.Evaluate(StateBuilder.Build([]), first.State, TestData.Now.AddHours(6)).Alerts);
    }

    [Fact]
    public void AtomicPersistencePreventsRepetitionAfterRestart()
    {
        using var directory = new TestDirectory();
        var state = State(TestData.Window(used: 95));
        var emitted = AlertPolicy.Evaluate(state, null, TestData.Now);
        var path = directory.PathFor("alerts.json");
        AtomicJsonFile.Write(path, emitted.State);
        var restored = AtomicJsonFile.Read<AlertState>(path);
        Assert.Empty(AlertPolicy.Evaluate(state, restored, TestData.Now).Alerts);
        Assert.Contains("Claude", emitted.Alerts[0].Text(TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.Contains("95 %", emitted.Alerts[0].Text(TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.Contains("remise", emitted.Alerts[0].Text(TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void AlertsUseAggregationAnchorRatherThanSelectedMeasurementsReset()
    {
        var anchor = TestData.Window(used: 80, measured: TestData.Now.AddMinutes(-2));
        var selected = anchor with { UsedPercent = 95, MeasuredAt = TestData.Now.AddMinutes(-1), ResetsAt = anchor.ResetsAt!.Value.AddSeconds(120) };
        var firstState = StateBuilder.Build(new[] { anchor, selected }.Select(TestData.Observation));
        var first = AlertPolicy.Evaluate(firstState, null, TestData.Now);
        Assert.Equal(anchor.ResetsAt, Assert.Single(first.State.Thresholds).ResetAnchor);
        var nextCluster = selected with { MeasuredAt = TestData.Now, ResetsAt = anchor.ResetsAt.Value.AddSeconds(240) };
        var nextState = StateBuilder.Build(new[] { anchor, selected, nextCluster }.Select(TestData.Observation));
        Assert.Equal(95, Assert.Single(AlertPolicy.Evaluate(nextState, first.State, TestData.Now).Alerts).Threshold);
    }
}
