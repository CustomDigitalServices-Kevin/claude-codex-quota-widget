using System.Globalization;
using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class ParserAndBlockageTests
{
    [Fact]
    public void SyntheticClaudeFixturePreservesMeasurementWindowsAndDecimalUsage()
    {
        var json = File.ReadAllText(TestData.Fixture("claude", "claude-quotas.d", "session-01.json"));
        var observation = ClaudeParser.Parse(json, "fixture", TestData.Now);
        Assert.Equal(2, observation.Windows.Length);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 40, 9, TimeSpan.Zero), observation.MeasuredAt);
        Assert.Equal(25, observation.Windows.Single(w => w.Type == "five_hour").UsedPercent);
        Assert.Equal(300, observation.Windows.Single(w => w.Type == "five_hour").DurationMinutes);
        Assert.Equal(50, observation.Windows.Single(w => w.Type == "seven_day").UsedPercent);
        Assert.Equal(10080, observation.Windows.Single(w => w.Type == "seven_day").DurationMinutes);
        // Derived from the synthetic fixture to reproduce the first-render null measurement.
        var unknown = json.Replace("\"2026-10-30T09:40:09Z\"", "null", StringComparison.Ordinal)
            .Replace("\"used_percentage\": 25", "\"used_percentage\": 25.5", StringComparison.Ordinal);
        var firstRender = ClaudeParser.Parse(unknown, "fixture", TestData.Now);
        Assert.Null(firstRender.MeasuredAt);
        Assert.All(firstRender.Windows, w => Assert.Null(w.MeasuredAt));
        Assert.Equal(25.5, firstRender.Windows.Single(w => w.Type == "five_hour").UsedPercent);
        Assert.Equal("inconnu", BlockageEvaluator.Evaluate("claude", [firstRender]).State);
    }

    [Fact]
    public void MissingWindowAndUnknownUsageAreNotReplacedWithZero()
    {
        var observation = ClaudeParser.Parse("""{"rate_limits":{"five_hour":{"used_percentage":null}}}""", "fixture", TestData.Now);
        var window = Assert.Single(observation.Windows);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.ResetsAt);
        Assert.Equal(300, window.DurationMinutes);
    }

    [Fact]
    public void JournalUsesFloatingPointAndInvariantCreditBalance()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var observation = TestData.ParseJournal(TestData.Journal(4.125));
            Assert.Equal(4.125, Assert.Single(observation.Windows).UsedPercent);
            Assert.Equal(12.5, observation.Credits?.Balance);
            Assert.False(observation.Credits?.Unlimited);
            Assert.Null(observation.OrdinaryUsageAllowed);
            Assert.Null(observation.ResetCredits);
            Assert.True(observation.SuccessfulTurn);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ConstructedAppServerExamplesFollowTheProvidedSchemaAndAreNotCapturedResponses()
    {
        // These examples were constructed from GetAccountRateLimitsResponse.schema.json, not captured.
        var json = File.ReadAllText(TestData.Fixture("constructed", "app-server-multiple-buckets.json"));
        var observations = CodexParser.ParseAppServer(json, "constructed", TestData.Now);
        Assert.Equal(2, observations.Length);
        Assert.All(observations, o => Assert.True(o.OrdinaryUsageAllowed));
        var codex = observations.Single(o => o.Counter == "codex");
        Assert.Equal(12, codex.Windows.Single(w => w.Type == "primary").UsedPercent);
        Assert.Equal(38, codex.Windows.Single(w => w.Type == "secondary").UsedPercent);
        Assert.Equal(12.5, codex.Credits?.Balance);
        Assert.Equal(JsonSupport.Fingerprint("constructed-account"), codex.Account);
        Assert.Equal(2, codex.ResetCredits?.AvailableCount);
        Assert.Single(Assert.IsType<ResetCredits>(codex.ResetCredits).Credits ?? []);
        var review = observations.Single(o => o.Counter == "code-review");
        Assert.Null(Assert.Single(review.Windows).ResetsAt);
        Assert.True(review.Credits?.Unlimited);
        Assert.Equal(3, StateBuilder.Build(observations).Codex.Windows.Length);

        var blocked = Assert.Single(CodexParser.ParseAppServer(
            File.ReadAllText(TestData.Fixture("constructed", "app-server-blocked.json")), "constructed", TestData.Now));
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("codex", [blocked]).State);
        Assert.Null(blocked.Windows[0].DurationMinutes);
        Assert.Null(blocked.ResetCredits?.Credits);
        Assert.Equal(0, blocked.ResetCredits?.AvailableCount);
    }

    [Fact]
    public void AppServerRejectsFractionalUsedPercentInsteadOfTreatingItAsAnInteger()
    {
        var observation = Assert.Single(CodexParser.ParseAppServer(
            """{"rateLimits":{"primary":{"usedPercent":1.25}}}""", "constructed", TestData.Now));
        Assert.Null(Assert.Single(observation.Windows).UsedPercent);
    }

    [Fact]
    public void AbsentFieldNullInfoOrZeroTokensNeverConfirmCodexRecovery()
    {
        var blocked = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(-2), reached: "rate_limit_reached"));
        var absent = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(-1), reachedPresent: false));
        var nullInfo = TestData.ParseJournal(TestData.Journal(successful: false));
        var zero = TestData.ParseJournal(TestData.Journal().Replace("\"total_tokens\":12", "\"total_tokens\":0", StringComparison.Ordinal));
        foreach (var next in new[] { absent, nullInfo, zero })
        {
            var state = BlockageEvaluator.Evaluate("codex", [blocked, next]);
            Assert.Equal("bloque", state.State);
            Assert.Equal(blocked.EvidenceAt, state.Since);
            Assert.Equal(Path.GetFullPath(blocked.Source), Path.GetFullPath(Assert.IsType<string>(state.Provenance)));
        }
        var positive = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(1)));
        Assert.Equal("non_bloque", BlockageEvaluator.Evaluate("codex", [blocked, absent, nullInfo, positive]).State);
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("codex", [blocked, positive with { MeasuredAt = blocked.MeasuredAt }]).State);
    }

    [Fact]
    public void ExplicitAppServerPermissionRestoresOnlyAfterTheBlockage()
    {
        var blocked = TestData.ParseJournal(TestData.Journal(reached: "rate_limit_reached"));
        var positive = blocked with { ReachedType = null, SuccessfulTurn = false, OrdinaryUsageAllowed = true,
            MeasuredAt = TestData.Now.AddMinutes(1) };
        Assert.Equal("non_bloque", BlockageEvaluator.Evaluate("codex", [blocked, positive]).State);
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("codex", [blocked, positive with { MeasuredAt = TestData.Now }]).State);
    }

    [Theory]
    [InlineData("percentage")]
    [InlineData("spend")]
    [InlineData("permission")]
    public void EachCodexBlockageSignalIsSufficient(string signal)
    {
        var observation = TestData.ParseJournal(TestData.Journal(signal == "percentage" ? 100 : 2));
        observation = observation with
        {
            SpendControlReached = signal == "spend" ? true : null,
            OrdinaryUsageAllowed = signal == "permission" ? false : null,
        };
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("codex", [observation]).State);
    }

    [Fact]
    public void ClaudeNeedsANewerDatedMeasurementForRecovery()
    {
        var blocked = TestData.Observation(TestData.Window(used: 100));
        var unknown = TestData.Observation(TestData.Window(used: 30) with { MeasuredAt = null, ObservedAt = TestData.Now.AddMinutes(1) });
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("claude", [blocked, unknown]).State);
        var positive = TestData.Observation(TestData.Window(used: 30, measured: TestData.Now.AddMinutes(2)));
        Assert.Equal("non_bloque", BlockageEvaluator.Evaluate("claude", [blocked, unknown, positive]).State);
        Assert.Equal("non_bloque", BlockageEvaluator.Evaluate("claude", [positive]).State);
        Assert.Equal("bloque", BlockageEvaluator.Evaluate("claude", [blocked, positive with { MeasuredAt = TestData.Now }]).State);
    }

    [Fact]
    public void DifferentAndUnknownAccountsAreNeverMerged()
    {
        var original = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(-1), used: 99));
        var target = TestData.ParseJournal(TestData.Journal(4)) with { Account = JsonSupport.Fingerprint("account-b") };
        var unknown = original with { Account = null, Source = "unknown-one" };
        var unknownTwo = unknown with { Source = "unknown-two" };
        var state = StateBuilder.Build([original, target, unknown, unknownTwo]);
        Assert.Equal(target.Account, state.Codex.Account);
        Assert.Equal(4, Assert.Single(state.Codex.Windows).UsedPercent);
        Assert.Equal(3, state.OtherAccounts.Length);
        Assert.Equal(2, state.OtherAccounts.Count(p => p.Account is null));
        var latestUnknown = StateBuilder.Build([target, unknown with { MeasuredAt = TestData.Now.AddMinutes(1) }]);
        Assert.Null(latestUnknown.Codex.Account);
        Assert.Empty(latestUnknown.Codex.Windows);
        Assert.Equal(2, latestUnknown.OtherAccounts.Length);
    }

    [Fact]
    public void SavedBlockageSurvivesSparseCollectionsAndOldEventsCannotRestoreIt()
    {
        var blocked = TestData.ParseJournal(TestData.Journal(reached: "rate_limit_reached"));
        var saved = StateBuilder.SaveBlockages(StateBuilder.Build([blocked]));
        var absent = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(1), reachedPresent: false));
        var oldPositive = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(-1)));
        Assert.Equal("bloque", StateBuilder.Build([oldPositive, absent], saved).Codex.Blockage.State);
        var positive = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(2)));
        Assert.Equal("non_bloque", StateBuilder.Build([positive], saved).Codex.Blockage.State);
    }

    [Fact]
    public void SwitchingAccountsDoesNotDiscardAnEarlierAccountsBlockage()
    {
        var blocked = TestData.ParseJournal(TestData.Journal(reached: "rate_limit_reached"));
        var saved = StateBuilder.SaveBlockages(StateBuilder.Build([blocked]));
        var other = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(1)))
            with { Account = JsonSupport.Fingerprint("account-b") };
        var switched = StateBuilder.Build([other], saved);
        Assert.Equal(other.Account, switched.Codex.Account);
        Assert.Equal("bloque", Assert.Single(switched.OtherAccounts).Blockage.State);
        var returning = TestData.ParseJournal(TestData.Journal(at: TestData.Now.AddMinutes(2), reachedPresent: false));
        var restored = StateBuilder.Build([returning], StateBuilder.SaveBlockages(switched));
        Assert.Equal(blocked.Account, restored.Codex.Account);
        Assert.Equal("bloque", restored.Codex.Blockage.State);
    }
}
